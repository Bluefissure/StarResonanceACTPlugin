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
        public Proto Decode(byte[] buf)
        {
            var proto = new Proto(buf);
            var reader = new CodedInputStream(buf);

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
                        break;
                    case 1: // fixed64
                        value = reader.ReadFixed64();
                        break;
                    case 2: // length-delimited (可能是嵌套 message 或 bytes)
                        var bytes = reader.ReadBytes().ToByteArray();
                        try
                        {
                            var decoded = Decode(bytes); // 递归解码嵌套
                            value = new Proto(bytes, decoded);
                        }
                        catch
                        {
                            value = new Proto(bytes, null); // 如果不是嵌套 message，则保留 bytes
                        }
                        break;
                    case 5: // fixed32
                        value = reader.ReadFixed32();
                        break;
                    default:
                        throw new Exception($"Unsupported wire type: {wireType}");
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
