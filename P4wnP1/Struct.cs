using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace P4wnP1
{
    public class Struct
    {
        public static UInt32 extractUInt32(List<byte> data)
        {
            byte[] arr = data.GetRange(0, 4).ToArray();
            if (BitConverter.IsLittleEndian) Array.Reverse(arr); //convert from network order to little endian if necessary

            data.RemoveRange(0, 4); //remove unneeded part from input data
            return BitConverter.ToUInt32(arr, 0); //retrun converted value
        }

        public static Int32 extractInt32(List<byte> data)
        {
            byte[] arr = data.GetRange(0, 4).ToArray();
            if (BitConverter.IsLittleEndian) Array.Reverse(arr); //convert from network order to little endian if necessary

            data.RemoveRange(0, 4); //remove unneeded part from input data
            return BitConverter.ToInt32(arr, 0); //retrun converted value
        }

        public static byte extractByte(List<byte> data)
        {
            byte res = data.ElementAt(0);
            data.RemoveRange(0, 1); //remove unneeded part from input data
            return res;
        }


        public static String extractNullTerminatedString(List<byte> data)
        {
            int pos_zero = data.FindIndex(x => x == 0);
            if (pos_zero > 0)
            {
                byte[] bytes = data.GetRange(0, pos_zero).ToArray();
                String result = Encoding.UTF8.GetString(bytes);
                data.RemoveRange(0, pos_zero + 1);

                return result;
            }
            else
            {
                return "";
            }
        }


        public static List<byte> packInt32(Int32 val)
        {
            return packInt32(val, null);
        }

        public static List<byte> packInt32(Int32 val, List<byte> data)
        {
            byte[] arr = BitConverter.GetBytes(val);
            if (BitConverter.IsLittleEndian) Array.Reverse(arr); //convert from  little endian to network order if necessary

            List<byte> result = new List<byte>(arr);

            if (data == null)
            {
                return result;
            }
            else
            {
                data.AddRange(result);
                return data;
            }
        }

        public static List<byte> packUInt32(UInt32 val)
        {
            return packUInt32(val, null);
        }

        public static List<byte> packUInt32(UInt32 val, List<byte> data)
        {
            byte[] arr = BitConverter.GetBytes(val);
            if (BitConverter.IsLittleEndian) Array.Reverse(arr); //convert from  little endian to network order if necessary

            List<byte> result = new List<byte>(arr);

            if (data == null)
            {
                return result;
            }
            else
            {
                data.AddRange(result);
                return data;
            }
        }

        public static List<byte> packByteArray(byte[] arr)
        {
            return packByteArray(arr, null);
        }

        public static List<byte> packByteArray(byte[] arr, List<byte> data)
        {
            List<byte> result = new List<byte>(arr);

            if (data == null)
            {
                return result;
            }
            else
            {
                data.AddRange(result);
                return data;
            }
        }

        public static List<byte> packByte(byte v)
        {
            return packByte(v, null);
        }

        public static List<byte> packByte(byte v, List<byte> data)
        {
            if (data != null)
            {
                data.Add(v);
                return data;
            }
            else
            {
                List<byte> new_data = new List<byte>();
                new_data.Add(v);
                return new_data;
            }
        }

        public static List<byte> packNullTerminatedString(string str)
        {
            return packNullTerminatedString(str, null);
        }

        public static List<byte> packNullTerminatedString(string str, List<byte> data)
        {
            //convert UTF8 to Byte[] and append 0x00
            List<byte> new_data = new List<byte> (Encoding.UTF8.GetBytes(str));
            new_data.Add((byte)0);

            if (data != null)
            {
                data.AddRange(new_data);
                return data;
            }
            else return new_data;
        }
    }
}
