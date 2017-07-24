using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace P4wnP1
{
    public class LinkLayer
    {
        public static bool DEBUG = true;

        private const int REPORTSIZE = 65;
        private const int PAYLOAD_MAX_SIZE = REPORTSIZE - 3; // max effective payload size in single report
        private const byte MAX_SEQ = 32; //how many sequence numbers are used by sender (largest possible SEQ number + 1)

        private const byte BITMASK = 63;
        private const byte BYTE1_FIN_BIT = 1 << 7;
        private const byte BYTE2_CONNECT_BIT = 1 << 7;

        private FileStream HIDin, HIDout;

        private Thread thread_in = null;
        private Thread thread_out = null;
        private Thread thread_timeout_watcher = null;

        private Byte state_last_valid_seq_received; // last seq number which has been valid (arrived in sequential order)
        private Boolean state_invalid_seq_received; // last SEQ number received, was invalid if this flag is set, the sender is informed about this with a resend request

        private System.Collections.Queue state_report_in_queue;
        private System.Collections.Queue state_report_out_queue;

        private Boolean state_connected;
        private AutoResetEvent state_streamReceivedEvent;

        private int out_queue_byte_size = 0;
        private Object out_queue_byte_size_lock;
        private ManualResetEvent out_queue_limit_not_reached;
        private const int MAX_OUT_QUEUE_SIZE = 30000; //Maximum size of out queue in bytes, before writing to queue blocks

        public delegate void LinkLayerTimeoutCallback(long dt);

        private LinkLayerTimeoutCallback timeoutCallbacks;
        public const int LINKLAYER_TIMEOUT_MILLIS = 1000;
        private Stopwatch timeoutStopwatch;

        public LinkLayer(FileStream HIDin, FileStream HIDout)
        {
            this.HIDin = HIDin;
            this.HIDout = HIDout;


            this.state_last_valid_seq_received = 12; // last seq number which has been valid (arrived in sequential order)
            this.state_invalid_seq_received = true; // last SEQ number received, was invalid if this flag is set, the sender is informed about this with a resend request

            
            this.state_report_in_queue = Queue.Synchronized(new Queue());
            this.state_report_out_queue = Queue.Synchronized(new Queue());

            this.state_connected = false;
            this.state_streamReceivedEvent = new AutoResetEvent(false);

            this.out_queue_byte_size_lock = new Object();
            this.out_queue_limit_not_reached = new ManualResetEvent(true);

            this.timeoutStopwatch = new Stopwatch();
        }

        public void registerTimeoutCallback(LinkLayerTimeoutCallback callback)
        {
            this.timeoutCallbacks += callback;
        }

        public void start()
        {
            if (!this.state_connected)
            {
                Console.WriteLine("LinkLayer.connect() has to be called before LinkLayer.start()");
                return;
            }

            this.thread_in = new Thread(new ThreadStart(this.HIDinThreadLoop));
            thread_in.Start();

            this.thread_out = new Thread(new ThreadStart(this.HIDOutThreadLoop));
            thread_out.Start();

            this.thread_timeout_watcher = new Thread(new ThreadStart(this.__detectTimeOut));
            thread_timeout_watcher.Start();

        }

        public void __detectTimeOut()
        {
            while (true)
            {
                Monitor.Enter(this.timeoutStopwatch);
                long millis = this.timeoutStopwatch.ElapsedMilliseconds;
                Monitor.Exit(this.timeoutStopwatch);
                if (millis > LinkLayer.LINKLAYER_TIMEOUT_MILLIS) this.timeoutCallbacks(millis);
                //Console.WriteLine(millis);
                Thread.Sleep(LinkLayer.LINKLAYER_TIMEOUT_MILLIS / 2);
            }
        }

        public void stop()
        {
            this.state_streamReceivedEvent.Set();
            //this.state_streamReceivedEvent.Close();
            if (this.thread_in != null) this.thread_in.Abort();
            if (this.thread_out != null) this.thread_out.Abort();
            if (this.thread_timeout_watcher != null) this.thread_timeout_watcher.Abort();
        }

        public void connect()
        {
            byte[] connect_request = new byte[REPORTSIZE];
            byte[] response = new byte[REPORTSIZE];
            connect_request[2] = (byte) (connect_request[2] | BYTE2_CONNECT_BIT);
            bool connected = false;

            while (!connected)
            {
                //wait till connect bit is received on a request with connect bit set
                HIDout.Write(connect_request, 0, REPORTSIZE);
                Console.WriteLine("Connect request sent");

                HIDin.Read(response, 0, REPORTSIZE);
                byte SEQ = (byte) (response[2] & BITMASK);
                bool CONNECT_BIT = (response[2] & BYTE2_CONNECT_BIT) != 0;

                if (CONNECT_BIT)
                {
                    connect_request[2] = (byte) (connect_request[2] | SEQ); // set ACK to SEQ and infor sender that we are in sync by sending correct ACK
                    HIDout.Write(connect_request, 0, REPORTSIZE);

                    //update the start seq number (this is all this handshake is about)
                    state_last_valid_seq_received = SEQ;
                    connected = true;
                }
                else
                {
                    print_debug(String.Format("SEQ {0} received on connect, but no connect response", SEQ));
                }
            }

            this.state_connected = true;
            this.state_invalid_seq_received = false;
        }

        private void HIDinThreadLoop()
        {

            Console.WriteLine("Starting receive loop continously reading HID ouput report");

            byte[] inbytes = new byte[REPORTSIZE];

            print_debug(String.Format("First SEQ received {0}", state_last_valid_seq_received));

            List<byte> stream = new List<byte>(); // used to re assemble a stream receiving via multiple reports

            Monitor.Enter(this.timeoutStopwatch);
            this.timeoutStopwatch.Start();
            Monitor.Exit(this.timeoutStopwatch);
            while (true)
            {
                HIDin.Read(inbytes, 0, REPORTSIZE);
                Monitor.Enter(this.timeoutStopwatch);
                this.timeoutStopwatch.Stop();
                this.timeoutStopwatch.Reset();
                this.timeoutStopwatch.Start();
                //Console.WriteLine(this.timeoutStopwatch.ElapsedTicks);
                Monitor.Exit(this.timeoutStopwatch);


                //extract header data
                byte LEN = (byte) (inbytes[1] & BITMASK); // payload length
                bool FIN = ((inbytes[1] & BYTE1_FIN_BIT) != 0);
                byte RECEIVED_SEQ = (byte)(inbytes[2] & BITMASK);

                byte next_valid_seq = (byte) (state_last_valid_seq_received + 1);
                if (next_valid_seq >= MAX_SEQ) next_valid_seq -= MAX_SEQ;

                //check if received SEQ is valid (in order)
                if (RECEIVED_SEQ == next_valid_seq)
                {
                    if (LEN > 0) // only handle reports with none empty payloads
                    {
                        //extract payload
                        List<byte> payload = new List<byte>(inbytes);
                        payload = payload.GetRange(3, LEN);

                        //append to stream
                        stream.AddRange(payload);
   
                        if (FIN)
                        {
                            state_report_in_queue.Enqueue(stream.ToArray());
                            stream.Clear();

                            //Fire stream received event
                            state_streamReceivedEvent.Set();
                        }
                    }

                    state_last_valid_seq_received = next_valid_seq;
                    state_invalid_seq_received = false;
                }
                else
                {
                    state_invalid_seq_received = true;
                }   
            }
        }

        private void HIDOutThreadLoop()
        {
            //int PAYLOAD_MAX_INDEX = PAYLOAD_MAX_SIZE - 1;

            byte BYTE1_RESEND_BIT = 1 << 6;
            
            


            Console.WriteLine("Starting write loop continously sending HID ouput report");

            List<byte> current_stream = new List<byte>(); //represents the remainder of the stream (from upper layer), which is currently processed (splitted into chunks, capable of being sent via HID report)
            List<byte> report_payload; //represents the payload which gets sent in a single report

            while (true)
            {
                bool last_report_of_stream = true; //if this bit is set, this means the report is the last one in a fragemented STREAM

                //check if there isn't a stream which hasn't been fully sent (remaining chunks in current_stream)
                if (current_stream.Count == 0)
                {
                    // no pending data in current stream, check if new full length stream is waiting in out queue
                    if (this.state_report_out_queue.Count > 0)
                    {
                        //dequeue next stream and put it into current_stream
                        current_stream.AddRange(PopOutputStream()); //type casting to byte array needed, as this isn't a generic Queue of type byte[] (synchronization wouldn't be possible)
                    }
                }

                //check if stream is small enough to fit into a single report
                if (current_stream.Count > PAYLOAD_MAX_SIZE)
                {
                    //no
                    report_payload = current_stream.GetRange(0, PAYLOAD_MAX_SIZE); //fetch MAX_SIZE bytes from the beginning
                    current_stream.RemoveRange(0, PAYLOAD_MAX_SIZE); //remove fetched bytes
                }
                else
                {
                    report_payload = current_stream.GetRange(0, current_stream.Count); //put the rest of the stream into payload
                    current_stream.Clear(); //empty out current_stream 
                }

                //if remaining data in current stream, set FIN bit to false
                if (current_stream.Count > 0) last_report_of_stream = false;

                /*
                 * Create the final report which should be send
                 */

                //if (report_payload.Count < 62 && report_payload.Count > 0) Console.WriteLine(String.Format("Output report with payload size {0} written",  report_payload.Count));

                //create LEN_FIN byte
                byte BYTE1_LEN_FLAGS = (byte) (report_payload.Count & BITMASK);
                if (last_report_of_stream) BYTE1_LEN_FLAGS += BYTE1_FIN_BIT;

                byte BYTE2_ACK = state_last_valid_seq_received; // set ACK byte to the last valid sequence received

                //set resend request bit, if last SEQ received was invalid
                if (state_invalid_seq_received)
                {
                    BYTE1_LEN_FLAGS += BYTE1_RESEND_BIT; // set RESEND BIT in byte1
                    BYTE2_ACK += 1; //increase the ACK by one (las valid SEQ + 1 is the next needed SEQ)
                    if (BYTE2_ACK > MAX_SEQ) BYTE2_ACK -= MAX_SEQ;
                }
                
                //prepend head to report (first byte is always 0, as it represents the unused HID report ID)
                report_payload.InsertRange(0, new byte[] { 0, BYTE1_LEN_FLAGS, BYTE2_ACK });
                byte[] report = new byte[REPORTSIZE];
                report_payload.CopyTo(report);

                /*
                 * Write report to HID device
                 */
                HIDout.Write(report, 0, REPORTSIZE);
            }
        }

        public int PendingInputStreamCount()
        {
            return this.state_report_in_queue.Count;
        }

        public int PendingOutputStreamCount()
        {
            return this.state_report_out_queue.Count;
        }

        public void WaitForInputStream()
        {
            while(true)
            {
                if (this.state_streamReceivedEvent.WaitOne(100)) return;
            }
        }

        public byte[] PopPendingInputStream()
        {
            return (byte[]) this.state_report_in_queue.Dequeue();
        }


        public void PushOutputStream(byte[] stream)
        {
            _PushOutputStream(stream, true);
        }

        public void PushOutputStreamNoBlock(byte[] stream)
        {
            _PushOutputStream(stream, false);
        }
        public void _PushOutputStream(byte[] stream, bool blockOnLimit)
        {
            //Console.WriteLine("WaitOne entered");
            this.out_queue_limit_not_reached.WaitOne();
            //Console.WriteLine("WaitOne passed");

            Monitor.Enter(this.out_queue_byte_size_lock);
            this.out_queue_byte_size += stream.Length;
            //Console.WriteLine(String.Format("PushOutputStream, new queue size {0}", this.out_queue_byte_size));
            if (this.out_queue_byte_size >= MAX_OUT_QUEUE_SIZE)
            {
                //Console.WriteLine("Signal reset");
                this.out_queue_limit_not_reached.Reset();
            }
            Monitor.Exit(this.out_queue_byte_size_lock);
            this.state_report_out_queue.Enqueue(stream);
            return;
        }

        public int OutputQueueByteSize
        {
            get
            {
                Monitor.Enter(this.out_queue_byte_size_lock);
                int res = out_queue_byte_size;
                Monitor.Exit(this.out_queue_byte_size_lock);
                return res;
            }

        }

        private byte[] PopOutputStream()
        {
            byte[] stream = (byte[])this.state_report_out_queue.Dequeue();
            Monitor.Enter(this.out_queue_byte_size_lock);
            this.out_queue_byte_size -= stream.Length;
            if (this.out_queue_byte_size < MAX_OUT_QUEUE_SIZE)
            {
                //Console.WriteLine("Signal set");
                this.out_queue_limit_not_reached.Set();
            }
            //Console.WriteLine(String.Format("PopOutputStream, remaining queue size {0}", this.out_queue_byte_size));
            Monitor.Exit(this.out_queue_byte_size_lock);
            return stream;
        }

        private static void print_debug(String text)
        {
            if (DEBUG) Console.WriteLine(String.Format("LinkLayer (DEBUG): {0}", text));
        }
    }
}
