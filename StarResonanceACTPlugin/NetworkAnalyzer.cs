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
using ZstdSharp;

namespace StarResonanceACTPlugin
{
    public class NetworkAnalyzer
    {
        private readonly PbDecoder pbDecoder = new();
        private readonly Dictionary<ulong, DamageData> totalDamage = new();
        private readonly Dictionary<ulong, CountData> totalCount = new();
        private readonly Dictionary<ulong, List<(long time, long damage)>> dpsWindow = new();
        private readonly Dictionary<ulong, long[]> damageTime = new();
        private string currentServer = "";

        private ulong userUid = 0;
        private byte[] tcpBuffer = Array.Empty<byte>();
        private uint tcpNextSeq = uint.MaxValue;
        private readonly Dictionary<uint, byte[]> tcpCache = new();
        private readonly Dictionary<uint, int> failCount = new();
        private readonly object tcpLock = new();

        private StreamWriter logWriter;
        internal ActHelper actHelper;

        public NetworkAnalyzer(StreamWriter logger)
        {
            logWriter = logger;
            actHelper = new(logWriter);
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
        public async Task RemoveOldPacket(uint seq)
        {
            try
            {
                await Task.Delay(10000);
                lock (tcpLock)
                {
                    if (tcpCache.ContainsKey(seq))
                        tcpCache.Remove(seq);
                }
            }
            catch
            {
                Log($"RemoveOldPacket failed: {seq}");
            }
        }
        /// <summary>
        /// TCP流重组 + 消息解码
        /// </summary>
        private void HandleData(IPv4Packet ipPacket, TcpPacket tcp)
        {
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
                //Log($"old tcpNextSeq: {tcpNextSeq} newSeq: {newSeq} len:{payload.Length}");

                // 拼接连续的 TCP 段
                while (tcpCache.ContainsKey(tcpNextSeq))
                {
                    var segment = tcpCache[tcpNextSeq];
                    tcpBuffer = tcpBuffer.Length == 0 ? segment : tcpBuffer.Concat(segment).ToArray();
                    tcpNextSeq += (uint)segment.Length;
                    RemoveOldPacket(tcpNextSeq - (uint)segment.Length);
                }
                //Log($"new tcpNextSeq: {tcpNextSeq} newSeq: {newSeq} len:{payload.Length}");
                int diff = (int)newSeq - (int)tcpNextSeq;
                if (!tcpCache.ContainsKey(tcpNextSeq) && (diff > 30))
                {
                    Log($"Missing Seq: {tcpNextSeq} newSeq:{newSeq} diff:{diff}");
                    failCount[tcpNextSeq] = failCount.ContainsKey(tcpNextSeq) ? failCount[tcpNextSeq] + 1 : 1;
                    if (failCount[tcpNextSeq] > 2)
                    {
                        Log($"Lost Seq: {tcpNextSeq}");
                        var toRemoveSeq = tcpNextSeq;
                        diff = (int)newSeq - (int)tcpNextSeq;
                        while (!tcpCache.ContainsKey(tcpNextSeq) && diff > 0)
                        {
                            tcpNextSeq++;
                            diff = (int)newSeq - (int)tcpNextSeq;
                        }
                        Log($"Reaching new Seq: {tcpNextSeq}");
                        new Task(async () =>
                        {
                            await Task.Delay(10000);
                            if (failCount.ContainsKey(toRemoveSeq))
                                failCount.Remove(toRemoveSeq);
                        }).Start();
                    }
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


        private void ClearTcpCache()
        {
            tcpBuffer = Array.Empty<byte>();
            tcpNextSeq = uint.MaxValue;
            tcpCache.Clear();
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
                //if (isZstd)
                //{
                //    Log($"bodyData Sig: {BitConverter.ToString(msgData.Skip(5).Take(6).ToArray())}");
                //    Log($"bodyData Protobuf: {BitConverter.ToString(bodyData)}");
                //    Log($"bodyData String: {Encoding.UTF8.GetString(bodyData)}");
                //}
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
                        foreach (var bObj in NormalizeToList(((Proto)body1Obj).Data))
                        {
                            if (bObj is not Dictionary<int, object> b) continue;
                            if (!b.TryGetValue(7, out var b7Obj) || ((Proto)b7Obj).Data is not Dictionary<int, object> b7) continue;
                            if (!b7.TryGetValue(2, out var hitsObj)) continue;

                            //if (isZstd)
                            //{
                            //    Log($"zstd hit: {isZstd}");
                            //}
                            foreach (var hitObj in NormalizeToList(hitsObj))
                            {
                                try
                                {
                                    if (((Proto)hitObj).Data is not Dictionary<int, object> hit) continue;

                                    int? skill = hit.TryGetValue(12, out var s) && !(s is Proto) ? Convert.ToInt32(s) : (int?)null;
                                    if (skill == null) continue;

                                    long damage = hit.TryGetValue(6, out var v) ? Convert.ToInt64(v) :
                                                  hit.TryGetValue(8, out var luckyVal) ? Convert.ToInt64(luckyVal) : 0;
                                    bool isLuck = hit.TryGetValue(8, out luckyVal) && Convert.ToInt64(luckyVal) != 0;
                                    bool isCrit = hit.TryGetValue(5, out var critVal) && Convert.ToInt32(critVal) != 0;
                                    long hpLessen = hit.TryGetValue(9, out var hp) ? Convert.ToInt64(hp) : 0;
                                    ulong operatorUid = hit.TryGetValue(21, out var op) ? ((ulong)(long)op >> 16) :
                                                        hit.TryGetValue(11, out var op2) ? ((ulong)(long)op2 >> 16) : 0;
                                    bool isPlayer = ((ulong)(hit.TryGetValue(21, out var p) ? (long)p :
                                                              hit.TryGetValue(11, out var p2) ? (long)p2 : 0) & 0xFFFF) == 640;

                                    if (!isPlayer || operatorUid == 0) continue;
                                    Log($"[HIT] UID={operatorUid} Skill={skill} Damage={damage} HpLessen={hpLessen} Crit={isCrit} Luck={isLuck} ");
                                    actHelper.AddDamageAttack(operatorUid.ToString(), skill.ToString(), isCrit, isLuck, (int)damage, DateTime.Now);
                                } catch
                                {
                                    Log($"Error parsing hit");
                                }
                            }
                        }
                    }

                } catch
                {
                    //Log($"Decode Error: ");
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

    }

}
