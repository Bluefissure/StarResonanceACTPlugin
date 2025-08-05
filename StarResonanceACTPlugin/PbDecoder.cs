using Google.Protobuf;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace StarResonanceACTPlugin
{
    public class PbDecoder
    {
        private StreamWriter logWriter;

        public PbDecoder(StreamWriter logWriter)
        {
            this.logWriter = logWriter;
        }
        private void Log(string message)
        {
            string timestamp = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
            logWriter.WriteLine(timestamp);
            Console.WriteLine(timestamp);
        }
        public Proto Decode(byte[] buf, bool verbose=false, int layer=0)
        {
            var proto = new Proto(buf);
            var reader = new CodedInputStream(buf);
            var layerSpace = new string('.', layer);

            while (!reader.IsAtEnd)
            {
                uint key = reader.ReadUInt32();
                int tag = (int)(key >> 3);
                int wireType = (int)(key & 0b111);

                object value = null;

                switch (wireType)
                {
                    case 0: // varint
                        value = reader.ReadInt64();
                        if (verbose)
                        {
                            Log($"{layerSpace}{tag}->varint: {value}");
                        }
                        break;
                    case 1: // fixed64
                        value = reader.ReadFixed64();
                        if (verbose)
                        {
                            Log($"{layerSpace}{tag}->fixed64: {value}");
                        }
                        break;
                    case 2: // length-delimited (可能是嵌套 message 或 bytes)
                        var bytes = reader.ReadBytes().ToByteArray();
                        try
                        {
                            if (verbose)
                            {
                                Log($"{layerSpace}{tag}->nested");
                            }
                            var decoded = Decode(bytes, verbose, layer + 1); // 递归解码嵌套
                            value = new Proto(bytes, decoded);
                        }
                        catch
                        {
                            value = new Proto(bytes, null); // 如果不是嵌套 message，则保留 bytes
                        }
                        break;
                    case 5: // fixed32
                        value = reader.ReadFixed32();
                        if (verbose)
                        {
                            Log($"{layerSpace}{tag}->fixed32: {value}");
                        }
                        break;
                    default:
                        var rawBytes = reader.ReadBytes().ToByteArray();
                        throw new Exception($"Unsupported wire type: {wireType} -> {BitConverter.ToString(rawBytes)}");
                }

                // 支持 repeated
                if (proto.Data.ContainsKey(tag))
                {
                    if (proto.Data[tag] is List<object> list)
                    {
                        list.Add(value);
                    }
                    else
                    {
                        proto.Data[tag] = new List<object> { proto.Data[tag], value };
                    }
                }
                else
                {
                    proto.Data[tag] = value;
                }
            }
            return proto;
        }
    }


    public class Proto
    {
        public byte[] Raw { get; }
        public Dictionary<int, object> Data { get; }

        public Proto(byte[] raw, Proto decoded = null)
        {
            Raw = raw;
            Data = decoded?.Data ?? new Dictionary<int, object>();
        }

        public object this[int key] => Data.ContainsKey(key) ? Data[key] : null;
    }
}
