using Advanced_Combat_Tracker;
using PacketDotNet;
using SharpPcap;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Markup;
using ZstdSharp;

namespace StarResonanceACTPlugin
{
    public class NetworkAnalyzer : IDisposable
    {
        private readonly PbDecoder pbDecoder;
        private readonly Dictionary<ulong, DamageData> totalDamage = new();
        private readonly Dictionary<ulong, CountData> totalCount = new();
        private readonly Dictionary<ulong, List<(long time, long damage)>> dpsWindow = new();
        private readonly Dictionary<ulong, long[]> damageTime = new();
        private string currentServer = "";

        private ulong userUid = 0;
        private byte[] tcpBuffer = Array.Empty<byte>();
        private uint tcpNextSeq = uint.MaxValue;
        private readonly SortedDictionary<uint, byte[]> tcpCache = new();
        private readonly Dictionary<uint, int> failCount = new();
        private readonly Dictionary<uint, DateTime> sequenceTimestamps = new();
        private readonly HashSet<uint> expectedSequences = new();
        private readonly Dictionary<ulong, string> uidToNameMap = new();
        private readonly object tcpLock = new();
        private readonly Timer cleanupTimer;
        private uint maxObservedGap = 0;
        private int consecutivePackets = 0;
        private volatile bool disposed = false;

        private StreamWriter logWriter;
        internal ActHelper actHelper;

        public NetworkAnalyzer(StreamWriter logger)
        {
            logWriter = logger;
            pbDecoder = new(logWriter);
            actHelper = new(logWriter);
            cleanupTimer = new Timer(CleanupStaleSequences, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
            ClearTcpCache();
        }

        private void Log(string message)
        {
            string timestamp = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
            logWriter.WriteLine(timestamp);
            Console.WriteLine(timestamp);
        }

        /// <summary>
        /// 供 SharpPcap 调用的包处理入口
        /// </summary>
        public void OnPacketArrival(object sender, PacketCapture e)
        {
            var rawPacket = e.GetPacket();
            var packet = Packet.ParsePacket(rawPacket.LinkLayerType, rawPacket.Data);
            var ip = packet.Extract<IPv4Packet>();
            var tcp = packet.Extract<TcpPacket>();

            if (ip != null && tcp != null && tcp.PayloadData?.Length > 0)
            {
                HandleData(ip, tcp);
            }
        }
        private void CleanupStaleSequences(object state)
        {
            if (disposed) return; // 防止在dispose后执行
            
            try
            {
                lock (tcpLock)
                {
                    if (disposed) return; // 双重检查
                    
                    var now = DateTime.Now;
                    var staleSequences = sequenceTimestamps
                        .Where(kvp => now - kvp.Value > TimeSpan.FromSeconds(10))
                        .Select(kvp => kvp.Key)
                        .ToList();

                    foreach (var seq in staleSequences)
                    {
                        tcpCache.Remove(seq);
                        sequenceTimestamps.Remove(seq);
                        failCount.Remove(seq);
                    }

                    if (staleSequences.Count > 0)
                    {
                        Log($"Cleaned up {staleSequences.Count} stale sequences");
                    }
                }
                
                // 检查战斗遭遇超时
                // actHelper?.CheckEncounterTimeout();
            }
            catch (ObjectDisposedException)
            {
                // Timer已被释放，忽略此异常
            }
            catch (Exception ex)
            {
                // 记录其他异常但不抛出，避免Timer崩溃
                if (!disposed)
                {
                    Log($"CleanupStaleSequences error: {ex.Message}");
                }
            }
        }
        /// <summary>
        /// TCP流重组 + 消息解码
        /// </summary>
        private void HandleData(IPv4Packet ipPacket, TcpPacket tcp)
        {
            if (disposed) return; // 防止在dispose后处理数据
            
            byte[] payload = tcp.PayloadData;

            string srcAddr = $"{ipPacket.SourceAddress}:{tcp.SourcePort}";
            string dstAddr = $"{ipPacket.DestinationAddress}:{tcp.DestinationPort}";
            string connection = $"{srcAddr} -> {dstAddr}";

            if (currentServer != connection)
            {
                if (TryIdentifyServer(payload, connection))
                {
                    Log($"[INFO] Identified Scene Server: {currentServer}");
                    ClearTcpCache();
                }
                return; // 未识别到服务器前直接跳过
            }

            lock (tcpLock)
            {
                // Log($"SequenceNumber: {tcp.SequenceNumber} PayloadLength={tcp.PayloadData?.Length}");
                // Log($"data: {BitConverter.ToString(tcp.PayloadData)}");
                // Log($"data string: {Encoding.UTF8.GetString(tcp.PayloadData)}");
                uint newSeq = tcp.SequenceNumber;
                if (tcpNextSeq == uint.MaxValue && payload.Length > 4 &&
                    BitConverter.ToUInt32(payload.Take(4).Reverse().ToArray(), 0) < 999999)
                {
                    tcpNextSeq = newSeq;
                }

                tcpCache[newSeq] = payload;
                sequenceTimestamps[newSeq] = DateTime.Now;
                
                // 动态调整间隙阈值
                if (consecutivePackets > 10)
                {
                    long currentGap = Math.Abs((long)newSeq - (long)tcpNextSeq);
                    if (currentGap < 0x80000000L) // 避免序列号回绕影响
                    {
                        maxObservedGap = Math.Max(maxObservedGap, (uint)currentGap);
                    }
                }
                //Log($"old tcpNextSeq: {tcpNextSeq} newSeq: {newSeq} len:{payload.Length}");

                // 智能拼接连续的 TCP 段
                int processedCount = 0;
                while (tcpCache.ContainsKey(tcpNextSeq))
                {
                    var segment = tcpCache[tcpNextSeq];
                    tcpBuffer = tcpBuffer.Length == 0 ? segment : tcpBuffer.Concat(segment).ToArray();
                    var oldSeq = tcpNextSeq;
                    tcpNextSeq += (uint)segment.Length;
                    
                    // 立即清理已处理的序列
                    tcpCache.Remove(oldSeq);
                    sequenceTimestamps.Remove(oldSeq);
                    failCount.Remove(oldSeq);
                    expectedSequences.Remove(oldSeq);
                    
                    processedCount++;
                    consecutivePackets++;
                }
                
                // 检测可能的间隙并预期后续序列
                if (processedCount > 0)
                {
                    DetectAndFillGaps();
                }
                //Log($"new tcpNextSeq: {tcpNextSeq} newSeq: {newSeq} len:{payload.Length}");
                
                // 使用智能间隙检测
                if (!tcpCache.ContainsKey(tcpNextSeq))
                {
                    HandleMissingSequence(newSeq);
                }

                //Log($"tcpNextSeq: {tcpNextSeq} tcp.SequenceNumber: {tcp.SequenceNumber}");

                // 按长度拆包并处理
                while (tcpBuffer.Length > 4)
                {
                    int len = ReadBigEndianInt32(tcpBuffer.Take(4).ToArray());
                    if (tcpBuffer.Length >= len)
                    {
                        var packet = tcpBuffer.Take(len).ToArray();
                        tcpBuffer = tcpBuffer.Skip(len).ToArray();
                        ProcessPacket(packet);
                    }
                    else break;
                }
            }
        }

        private bool TryIdentifyServer(byte[] buf, string connection)
        {
            if (buf.Length <= 10) return false;

            var data = buf.Skip(10).ToArray();
            using var ms = new MemoryStream(data);
            using var reader = new BinaryReader(ms);

            while (ms.Position < ms.Length)
            {
                if (ms.Length - ms.Position < 4) break;
                uint len = ReadBigEndianUInt32(reader);
                if (ms.Length - ms.Position < len - 4) break;

                var msgData = reader.ReadBytes((int)len - 4);
                var signature = new byte[] { 0x00, 0x63, 0x33, 0x53, 0x42, 0x00 }; // c3SB??

                // 直接匹配签名
                if (msgData.Length < 5 + signature.Length) break;
                var actualSig = msgData.Skip(5).Take(signature.Length);
                if (!actualSig.SequenceEqual(signature))
                    return false;
                //Log($"Got Sig: {Encoding.UTF8.GetString(actualSig.ToArray())}");
                //Log($"msgData Protobuf: {BitConverter.ToString(msgData.ToArray())}");

                // ✅ 先更新服务器，再尝试UID
                currentServer = connection;
                ClearTcpCache();
                Console.WriteLine($"[INFO] Got Scene Server Address: {connection}");
                /*
                try
                {
                    if (msgData[17] == 0x2e)
                    {
                        Log($"body Protobuf: {BitConverter.ToString(msgData.Skip(18).ToArray())}");
                        Log($"body String: {Encoding.UTF8.GetString(msgData.Skip(18).ToArray())}");
                        var body = (Dictionary<int, object>)pbDecoder.Decode(msgData.Skip(18).ToArray());
                        if (body.TryGetValue(5, out var uidVal))
                        {
                            userUid = (ulong)((ulong)uidVal >> 16);
                            Log($"[INFO] Got player UID: {userUid}");
                        }
                    }
                }
                catch {}
                */

                return true; // ✅ 一旦匹配签名就算成功
            }
            return false;
        }


        private void HandleMissingSequence(uint newSeq)
        {
            long diff = (long)newSeq - (long)tcpNextSeq;
            
            // 处理序列号回绕
            if (diff < -0x80000000L) diff += 0x100000000L;
            else if (diff > 0x80000000L) diff -= 0x100000000L;
            
            // 动态阈值：基于观察到的最大间隙，最小30字节
            uint dynamicThreshold = Math.Max(30, Math.Min(maxObservedGap * 2, 1500)); // 最大1500字节（标准MTU）
            
            if (diff > dynamicThreshold)
            {
                Log($"Missing Seq: {tcpNextSeq} newSeq:{newSeq} diff:{diff} threshold:{dynamicThreshold}");
                
                // 检查是否为预期的序列
                if (expectedSequences.Contains(tcpNextSeq))
                {
                    failCount[tcpNextSeq] = failCount.ContainsKey(tcpNextSeq) ? failCount[tcpNextSeq] + 1 : 1;
                    
                    // 更激进的跳过策略
                    if (failCount[tcpNextSeq] > 1) // 减少等待次数
                    {
                        Log($"Lost expected Seq: {tcpNextSeq}, performing gap recovery");
                        PerformGapRecovery(newSeq);
                    }
                }
                else
                {
                    // 非预期序列，立即尝试恢复
                    PerformGapRecovery(newSeq);
                }
            }
        }
        
        private void PerformGapRecovery(uint newSeq)
        {
            // 查找最佳跳跃点
            var sortedKeys = tcpCache.Keys.ToList();
            
            // 尝试找到连续序列的开始
            uint bestSeq = tcpNextSeq;
            for (int i = 0; i < sortedKeys.Count - 1; i++)
            {
                uint current = sortedKeys[i];
                uint next = sortedKeys[i + 1];
                
                if (current >= tcpNextSeq && next - current <= 1500) // 连续或小间隙
                {
                    bestSeq = current;
                    break;
                }
            }
            
            if (bestSeq == tcpNextSeq && sortedKeys.Count > 0)
            {
                // 没找到好的跳跃点，跳到第一个可用序列
                bestSeq = sortedKeys.First(seq => seq >= tcpNextSeq);
            }
            
            if (bestSeq != tcpNextSeq)
            {
                Log($"Gap recovery: jumped from {tcpNextSeq} to {bestSeq}");
                tcpNextSeq = bestSeq;
                
                // 清理过期的失败计数和预期序列
                var keysToRemove = failCount.Keys.Where(k => k < tcpNextSeq).ToList();
                foreach (var key in keysToRemove)
                {
                    failCount.Remove(key);
                }
                
                expectedSequences.RemoveWhere(seq => seq < tcpNextSeq);
                consecutivePackets = 0; // 重置连续计数
            }
        }
        
        private void DetectAndFillGaps()
        {
            // 预测下一个可能的序列号范围
            var sortedKeys = tcpCache.Keys.Where(seq => seq > tcpNextSeq).Take(5).ToList();
            
            foreach (var seq in sortedKeys)
            {
                long gap = seq - tcpNextSeq;
                if (gap > 0 && gap <= 1500) // 合理的间隙大小
                {
                    // 标记为预期序列
                    for (uint expected = tcpNextSeq; expected < seq; expected += 1460) // 典型TCP段大小
                    {
                        expectedSequences.Add(expected);
                    }
                }
            }
            
            // 限制预期序列集合大小
            if (expectedSequences.Count > 100)
            {
                var toRemove = expectedSequences.Where(seq => seq < tcpNextSeq).ToList();
                foreach (var seq in toRemove)
                {
                    expectedSequences.Remove(seq);
                }
            }
        }
        
        private void ClearTcpCache()
        {
            tcpBuffer = Array.Empty<byte>();
            tcpNextSeq = uint.MaxValue;
            tcpCache.Clear();
            sequenceTimestamps.Clear();
            failCount.Clear();
            expectedSequences.Clear();
            uidToNameMap.Clear();
            maxObservedGap = 0;
            consecutivePackets = 0;
        }

        /// <summary>
        /// 单个数据包解析与伤害统计
        /// </summary>
        private void ProcessPacket(byte[] buf)
        {
            if (buf.Length < 32) return;
            bool isZstd = false;
            // Zstd压缩判断与解压
            if ((buf[4] & 0x80) != 0)
            {
                Log("Zstd decompress");
                isZstd = true;
                using var zstd = new Decompressor();
                var decompressed = zstd.Unwrap(buf.Skip(10).ToArray()).ToArray();
                buf = buf.Take(10).Concat(decompressed).ToArray();
            }

            var data = buf.Skip(10).ToArray();
            if (data.Length == 0) return;

            using var ms = new MemoryStream(data);
            using var reader = new BinaryReader(ms);

            while (ms.Position < ms.Length)
            {
                if (ms.Length - ms.Position < 4) break;
                uint msgLen = ReadBigEndianUInt32(reader);
                if (ms.Length - ms.Position < msgLen - 4) break;

                var msgData = reader.ReadBytes((int)(msgLen - 4));
                var bodyData = msgData.Skip(18).ToArray();
                try
                {
                    var body = pbDecoder.Decode(bodyData).Data;

                    if (body.TryGetValue(1, out var body1Obj))
                    {
                        // 提取玩家 UID
                        if (msgData[17] == 0x2e)
                        {
                            Dictionary<int, object> body1 = ((Proto)body1Obj).Data;
                            if (body1.TryGetValue(5, out var uidVal))
                            {
                                ulong uid = (ulong)((long)uidVal) >> 16;
                                if (userUid != uid)
                                {
                                    userUid = uid;
                                    Log($"[INFO] Got player UID: {uid}");
                                }

                            }
                        }

                        // 解析 body[1] → b[7][2]
                        foreach (var bObj in NormalizeToList(body1Obj))
                        {
                            if (bObj is not Proto) continue;
                            var bDict = ((Proto)bObj).Data;
                            if (!bDict.TryGetValue(7, out var b7Obj)) continue;
                            foreach (var b7 in NormalizeToList(b7Obj))
                            {
                                if (b7 is not Proto) continue;
                                var b7Dict = ((Proto)b7).Data;
                                if (!b7Dict.TryGetValue(2, out var hitsObj)) continue;
                                var hitList = NormalizeToList(hitsObj);
                                var hitListLen = hitList.Count;
                                //foreach (var hitObj in hitList)
                                for (int idx = 0; idx < hitListLen; idx ++)
                                {
                                    var hitObj = hitList[idx];
                                    if (hitObj is not Proto) continue;
                                    try
                                    {
                                        var hit = ((Proto)hitObj).Data;
                                        
                                        int? skill = hit.TryGetValue(12, out var s) && !(s is Proto) ? Convert.ToInt32(s) : (int?)null;
                                        if (skill == null) {
                                            continue;
                                        }


                                        //Log($"[DEBUG] Raw hit data:");
                                        //foreach (var kvp in hit)
                                        //{
                                        //    string valueStr = "";
                                        //    if (kvp.Value is Proto proto)
                                        //    {
                                        //        Log($"  [{kvp.Key}] = Proto (expanded):");
                                        //        foreach (var subKvp in proto.Data)
                                        //        {
                                        //            string subValueStr = "";
                                        //            if (subKvp.Value is Proto subProto)
                                        //            {
                                        //                subValueStr = $"Proto[{string.Join(",", subProto.Data.Keys)}]";
                                        //            }
                                        //            else
                                        //            {
                                        //                subValueStr = subKvp.Value?.ToString() ?? "null";
                                        //            }
                                        //            Log($"    [{subKvp.Key}] = {subValueStr} ({subKvp.Value?.GetType().Name})");
                                        //        }
                                        //    }
                                        //    else
                                        //    {
                                        //        valueStr = kvp.Value?.ToString() ?? "null";
                                        //        Log($"  [{kvp.Key}] = {valueStr} ({kvp.Value?.GetType().Name})");
                                        //    }
                                        //}

                                        long damage = hit.TryGetValue(6, out var v) ? Convert.ToInt64(v) :
                                                      hit.TryGetValue(8, out var luckyVal) ? Convert.ToInt64(luckyVal) : 0;
                                        bool isHeal = hit.TryGetValue(4, out var healType) && Convert.ToInt64(healType) != 0;
                                        bool isLuck = hit.TryGetValue(8, out luckyVal) && Convert.ToInt64(luckyVal) != 0;
                                        bool isCrit = hit.TryGetValue(5, out var critVal) && Convert.ToInt32(critVal) != 0;
                                        bool isKill = hit.TryGetValue(17, out var killVal) && Convert.ToInt32(killVal) != 0;
                                        long hpLessen = hit.TryGetValue(9, out var hp) ? Convert.ToInt64(hp) : 0;
                                        
                                        // 解析坐标信息 (字段19)
                                        string coordinates = "Unknown";
                                        if (hit.TryGetValue(19, out var coordObj) && coordObj is Proto coordProto)
                                        {
                                            var coordData = coordProto.Data;
                                            if (coordData.TryGetValue(1, out var x) && 
                                                coordData.TryGetValue(2, out var y) && 
                                                coordData.TryGetValue(3, out var z))
                                            {
                                                // 将UInt32解释为float坐标
                                                float xCoord = BitConverter.ToSingle(BitConverter.GetBytes(Convert.ToUInt32(x)), 0);
                                                float yCoord = BitConverter.ToSingle(BitConverter.GetBytes(Convert.ToUInt32(y)), 0);
                                                float zCoord = BitConverter.ToSingle(BitConverter.GetBytes(Convert.ToUInt32(z)), 0);
                                                coordinates = $"({xCoord:F2}, {yCoord:F2}, {zCoord:F2})";
                                            }
                                        }

                                        ulong operatorUid = hit.TryGetValue(21, out var op) ? ((ulong)(long)op >> 16) :
                                                            hit.TryGetValue(11, out var op2) ? ((ulong)(long)op2 >> 16) : 0;
                                        ulong targetUid = bDict.TryGetValue(1, out var targetIdObj) ? ((ulong)(long)targetIdObj) : 0;
                                        bool isPlayer = ((ulong)(hit.TryGetValue(21, out var p) ? (long)p :
                                                                  hit.TryGetValue(11, out var p2) ? (long)p2 : 0) & 0xFFFF) == 640;

                                        if (!isPlayer || operatorUid == 0) continue;

                                        // 检查是否有已知的角色名
                                        // string playerName = uidToNameMap.TryGetValue(operatorUid, out string name) ? name : operatorUid.ToString();
                                        string opType = "HIT";
                                        if (isKill)
                                        {
                                            opType = "KILL";
                                        }
                                        if (isHeal)
                                        {
                                            opType = "HEAL";
                                        }

                                        Log($"[{opType}] UID={operatorUid} Target={targetUid} Skill={skill} Damage={damage} Coords={coordinates} Crit={isCrit} Luck={isLuck}");


                                        actHelper.AddDamageAttack($"Player#{operatorUid}", $"Enemy#{targetUid}", skill.ToString(), isCrit, isLuck, isHeal, (int)damage, DateTime.Now);
                                    }
                                    catch
                                    {
                                        Log($"Error parsing hit");
                                    }
                                }

                            }
                            
                        }
                    }
                } catch (Exception e)
                {
                    Log($"Decode Error: {e.ToString()}");
                }
            }
        }

        private static List<object> NormalizeToList(object obj)
        {
            return obj switch
            {
                List<object> list => list,
                _ => new List<object> { obj }
            };
        }
        private static uint ReadBigEndianUInt32(byte[] bytes)
        {
            if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
            return BitConverter.ToUInt32(bytes, 0);
        }

        private static uint ReadBigEndianUInt32(BinaryReader reader)
        {
            var bytes = reader.ReadBytes(4);
            return ReadBigEndianUInt32(bytes);
        }

        private static int ReadBigEndianInt32(byte[] bytes)
        {
            if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
            return BitConverter.ToInt32(bytes, 0);
        }

        private static int ReadBigEndianInt32(BinaryReader reader)
        {
            var bytes = reader.ReadBytes(4);
            return ReadBigEndianInt32(bytes);
        }

        private class DamageData
        {
            public long Normal, Critical, Lucky, CritLucky, HpLessen, Total;
        }

        private class CountData
        {
            public int Normal, Critical, Lucky, Total;
        }

        public void Dispose()
        {
            if (disposed) return;
            
            disposed = true;
            
            try
            {
                cleanupTimer?.Dispose();
            }
            catch (Exception ex)
            {
                Log($"Timer dispose error: {ex.Message}");
            }
            
            lock (tcpLock)
            {
                ClearTcpCache();
            }
        }
    }

}
