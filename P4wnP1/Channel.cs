using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace P4wnP1
{
    public class Channel
    {
        public enum Encodings { UTF8, BYTEARRAY };
        public enum Types { IN, OUT, BIDIRECTIONAL };

        public Encodings encoding { get; }
        public Types type { get; }
        public UInt32 ID { get; }
        private bool _isLinked;
        private bool _shouldBeClosed = false;

        public bool shouldBeClosed
        {
            get
            {
                return _shouldBeClosed;
            }
            set
            {
                _shouldBeClosed = value;
                // if this is an output channel, inform Callback listeners about the change, as processing could be needed
                if (this._shouldBeClosed) this.onChannelDirty?.Invoke();
            }
        }

        public bool isLinked
        {
            get
            {
                return _isLinked;
            }
            set
            {
                _isLinked = value;
                // if this is an output channel, inform Callback listeners about the change, as processing could be needed
                if (this.type != Types.IN) this.onOutDirty();
            }
        } //Channel is known on python side, if this isn't be true; data sent could be ignored by the other end


        protected Queue in_queue = null;
        protected Queue out_queue = null;
        protected Queue out_queue_backup = null;

        protected static UInt32 next_id = 0;
        protected CallbackOutputProcessingNeeded onOutDirty;
        protected CallbackChannelProcessingNeeded onChannelDirty;

        public delegate void CallbackOutputProcessingNeeded(); //the delgate is used, to inform somebody that output processing is needed (in our case the LinkLayer)
        public delegate void CallbackChannelProcessingNeeded(); //the delgate is used, to inform somebody that output processing is needed (in our case the LinkLayer)

        public Channel(Encodings encoding, Types type, CallbackOutputProcessingNeeded onOutDirty, CallbackChannelProcessingNeeded onChannelDirty)
        {
            this.ID = next_id;
            next_id++;
            this.encoding = encoding;
            this.type = type;
            this.onOutDirty = onOutDirty;
            this.onChannelDirty = onChannelDirty;
            this.isLinked = false;
            // if IN channel or BIDIRECTIONAL channel, generate inbound queue
            if (this.type != Types.OUT) this.in_queue = Queue.Synchronized(new Queue());

            // if OUT channel or BIDIRECTIONAL channel, generate inbound queue
            if (this.type != Types.IN)
            {
                this.out_queue = Queue.Synchronized(new Queue());
                this.out_queue_backup = Queue.Synchronized(new Queue());
            }
        }

        
        public virtual bool hasPendingInData()
        {
            if (this.in_queue == null) return false;
            return (this.in_queue.Count > 0);
        }

        public virtual bool hasPendingOutData()
        {
            if (this.out_queue == null) return false;
            return (this.out_queue.Count > 0);
        }

        public virtual byte[] read()
        {
            //read data from IN or BIDIRECTIONAL (input) channel to application layer
            //return null if no data available
            if (this.type == Types.OUT) return null; // can not read from out channel
            if (this.in_queue.Count > 0) return (byte[]) this.in_queue.Dequeue(); //return data if available
            else return null; //otherwise return null
        }

        public virtual void write(byte[] data)
        {
            //write data to OUT or BIDIRECTIONAL (output) channel from application layer
            if (this.type == Types.IN) return; // can not write to IN channel
            this.out_queue.Enqueue(data);

            //Inform about needed output processing
            this.onOutDirty();

        }

        public virtual byte[] DequeueOutput()
        {
            //dequeue data from OUT or BIDIRECTIONAL (output) channel for further processing
            //null if no data
            if (this.type == Types.IN) return null; // can not dequeue data from INPUT channel for processing
            if (this.out_queue.Count > 0) return (byte[])this.out_queue.Dequeue(); //return data if available
            else return null; //otherwise return null
        }

        public virtual void EnqueueInput(byte[] data)
        {
            //enqueue data in IN or BIDIRECTIONAL (input) channel from processing layer
            if (this.type == Types.OUT) return; // can not enqueue data in OUT channel for processing
            this.in_queue.Enqueue(data);
        }

        public virtual void onClose()
        {
            
        }
    }

    public class StreamChannel : Channel
    {
        private Stream stream;

        private enum CH_MSG_TYPE
        {
            CHANNEL_CONTROL_REQUEST_STATE = 1,
            CHANNEL_CONTROL_REQUEST_READ = 2,
            CHANNEL_CONTROL_REQUEST_FLUSH = 3,
            CHANNEL_CONTROL_REQUEST_CLOSE = 4,
            CHANNEL_CONTROL_REQUEST_POSITION = 5,
            CHANNEL_CONTROL_REQUEST_LENGTH = 6,
            CHANNEL_CONTROL_REQUEST_READ_TIMEOUT = 7,
            CHANNEL_CONTROL_REQUEST_WRITE_TIMEOUT = 8,
            CHANNEL_CONTROL_REQUEST_SEEK = 9
        }

        public StreamChannel(Stream stream, CallbackOutputProcessingNeeded onOutDirty, CallbackChannelProcessingNeeded onChannelDirty) : base(Channel.Encodings.BYTEARRAY, Channel.Types.BIDIRECTIONAL, onOutDirty, onChannelDirty)
        {
            this.stream = stream;
        }

        public override void onClose()
        {
            base.onClose();
            stream.Flush();
            stream.Dispose();
            stream.Close();
        }

        public override void EnqueueInput(byte[] data)
        {
            List<byte> data_list = new List<byte>(data);
            byte data_type = Struct.extractByte(data_list);
            if (data_type == 0) //normal data
            {
                data = data_list.ToArray();
                this.stream.Write(data, 0, data.Length);
                Console.WriteLine(String.Format("Written to stream: {0}", data));
            }
            else dispatchControlMessage(data_list);
        }

        private void dispatchControlMessage(List<byte> msg)
        {
            /*
             * This method is called from the input thread (not the processing thread), so time consuming or
             * blocking tasks mustn't be run here
             */
            CH_MSG_TYPE ch_msg_type = (CH_MSG_TYPE) Struct.extractUInt32(msg);
            switch (ch_msg_type)
            {
                case CH_MSG_TYPE.CHANNEL_CONTROL_REQUEST_STATE:
                    Console.WriteLine(String.Format("Received STATE request for StreamChannel {0}", this.ID));
                    break;
                case CH_MSG_TYPE.CHANNEL_CONTROL_REQUEST_READ:
                    int count = Struct.extractInt32(msg);
                    int timeout = Struct.extractInt32(msg);
                    Console.WriteLine(String.Format("Received READ request for StreamChannel {0}, count {1}, timeout {2}", this.ID, count, timeout));
                    
                    break;
                case CH_MSG_TYPE.CHANNEL_CONTROL_REQUEST_FLUSH:
                    Console.WriteLine(String.Format("Received FLUSH request for StreamChannel {0}", this.ID));
                    stream.Flush();
                    break;
                case CH_MSG_TYPE.CHANNEL_CONTROL_REQUEST_CLOSE:
                    Console.WriteLine(String.Format("Received CLOSE request for StreamChannel {0}", this.ID));
                    this.shouldBeClosed = true; //no processing overhead
                    break;
                case CH_MSG_TYPE.CHANNEL_CONTROL_REQUEST_POSITION:
                    Console.WriteLine(String.Format("Received POSITION request for StreamChannel {0}", this.ID));
                    break;
                case CH_MSG_TYPE.CHANNEL_CONTROL_REQUEST_LENGTH:
                    Console.WriteLine(String.Format("Received LENGTH request for StreamChannel {0}", this.ID));
                    break;
                case CH_MSG_TYPE.CHANNEL_CONTROL_REQUEST_READ_TIMEOUT:
                    Console.WriteLine(String.Format("Received READ_TIMEOUT request for StreamChannel {0}", this.ID));
                    break;
                case CH_MSG_TYPE.CHANNEL_CONTROL_REQUEST_WRITE_TIMEOUT:
                    Console.WriteLine(String.Format("Received WRITE_TIMEOUT request for StreamChannel {0}", this.ID));
                    break;
                case CH_MSG_TYPE.CHANNEL_CONTROL_REQUEST_SEEK:
                    int offset = Struct.extractInt32(msg);
                    int origin = Struct.extractInt32(msg);
                    Console.WriteLine(String.Format("Received SEEK request for StreamChannel {0}, count {1}, timeout {2}", this.ID, offset, origin));
                    break;
                default:
                    Console.WriteLine(String.Format("Received unknown channel message for StreamChannel {0}", this.ID));
                    break;
            }
        }
    }

    public class FileChannel : Channel
    {
        private static bool PROTECT_USER = true; //if true, user is protected from overwriting files accidently

        private FileMode fileMode;
        private FileAccess fileAccess;
        private String requestedAccessMode = null;
        private byte targetMode = 0;
        private String filename = null;
        private FileStream fs;

        public FileChannel(CallbackOutputProcessingNeeded onOutDirty, CallbackChannelProcessingNeeded onChannelDirty, String filename, String accessMode, byte targetMode, bool force = false) : base(Encodings.BYTEARRAY, Types.BIDIRECTIONAL, onOutDirty, onChannelDirty)
        {
            this.requestedAccessMode = accessMode;
            this.targetMode = targetMode;
            this.filename = filename;
            

            bool userProtect = FileChannel.PROTECT_USER && !force;
            if (targetMode == 0)
            {
                //is targeting disc
                this.fileAccess = FileChannel.translateToAccessMode(requestedAccessMode);
                this.fileMode = FileChannel.translateToFileMode(requestedAccessMode, userProtect);

                this.fs = File.Open(this.filename, this.fileMode, this.fileAccess); //Exceptions are catched by caller and delivered to remote server

            }
            else if(targetMode == 1)
            {
                //is targeting in-memory file transfer (write only)
            }
            else
            {
                //unsupported target mode
            }
        }

        private static FileMode translateToFileMode(String accessMode, bool userProtect = true)
        {
            /*
            FILEMODE_READ = "rb" --> Open
            FILEMODE_WRITE = "wb" --> CreateNew (userProtect), Create (!userProtect)
            FILEMODE_READWRITE = "r+b" --> Open (userProtect), OpenOrCreate (!userProtect)
            FILEMODE_APPEND = "ab" --> Append
            */
            if (accessMode == "rb") return FileMode.Open;
            else if (accessMode == "wb" && userProtect) return FileMode.CreateNew;
            else if (accessMode == "wb" && !userProtect) return FileMode.Create;
            else if (accessMode == "r+b" && userProtect) return FileMode.Open;
            else if (accessMode == "r+b" && !userProtect) return FileMode.OpenOrCreate;
            else if (accessMode == "ab") return FileMode.Append;
            else throw new ArgumentException(String.Format("Unknown file access mode: '{0}'", accessMode));
        }

        private static FileAccess translateToAccessMode(String accessMode)
        {
            /*
            FILEMODE_READ = "rb" --> READ
            FILEMODE_WRITE = "wb" --> WRITE
            FILEMODE_READWRITE = "r+b" --> READWRITE
            FILEMODE_APPEND = "ab" --> WRITE
            */
            if (accessMode == "rb") return FileAccess.Read;
            else if (accessMode == "wb") return FileAccess.Write;
            else if (accessMode == "r+b") return FileAccess.ReadWrite;
            else if (accessMode == "ab") return FileAccess.Write;
            else throw new ArgumentException(String.Format("Unknown file access mode: '{0}'", accessMode));

        }
    }

    public class ProcessChannel : Channel
    {
        private const int READ_BUFFER_SIZE = 1024;

        private Stream stream;
        private Process process;

        private Object lock_output;
        private Thread thread_out = null;
        List<byte> accumulation_out;

        private int bufferedOutBytes;

        //const int CHUNK_SIZE = 59996;
        const int CHUNK_SIZE = 3096;

        private onProcExitedCallback callbacksExit = null;

        public delegate void onProcExitedCallback(Process proc);

        

        public ProcessChannel(Process process, Stream stream, Encodings encoding, Types type, CallbackOutputProcessingNeeded onOutDirty, CallbackChannelProcessingNeeded onChannelDirty) : base(encoding, type, onOutDirty, onChannelDirty)
        {
            this.stream = stream;
            this.process = process;
            this.lock_output = new object();
            this.bufferedOutBytes = 0;

            //if this is an out channel, we create a thread permannently reading from the stream
            if (this.type != Types.IN)
            {
                this.accumulation_out = new List<byte>();
                this.thread_out = new Thread(new ThreadStart(this.passThruOut));
                thread_out.Start();
            }
        }

        public void registerProcExitedCallback(onProcExitedCallback callback)
        {
            this.callbacksExit += callback;
        }

        public void passThruOut()
        {
            byte[] readBuf = new byte[READ_BUFFER_SIZE];
            List<byte> readbufCopy = new List<byte>();

            

            while (!this.process.HasExited)
            {
                //This could be a CPU consuming loop if much output is produced and couldn't be delivered fast enough
                //as our theoretical maximum transfer rate is 60000 Bps we introduce a sleep when the out_queue_size exceeds 60000 bytes
                if (this.bufferedOutBytesCount() > 60000) Thread.Sleep(50); // 50 ms sleep

                int count = this.stream.Read(readBuf, 0, readBuf.Length);
                
                // trim data down to count
                readbufCopy.AddRange(readBuf);
                //readbufCopy.GetRange(0, count);
                readbufCopy.RemoveRange(count, READ_BUFFER_SIZE - count);

                //Console.WriteLine(String.Format("ProcessChannel read: {0}", Encoding.UTF8.GetString(readbufCopy.ToArray())));

                //transfer data into accumualtion buffer (blocking other thread access)
                this.write(readbufCopy.ToArray());

                readbufCopy.Clear();
            }
            this.callbacksExit?.Invoke(this.process);
            this.shouldBeClosed = true;
            
        }

        public override byte[] DequeueOutput()
        {
            //Console.WriteLine("Entering Monitor: dequeue");
            Monitor.Enter(this.lock_output);
            byte[] res = base.DequeueOutput();
            this.bufferedOutBytes -= res.Length;
            Monitor.Exit(this.lock_output);
            //Console.WriteLine("Exitting Monitor: dequeue");
            return res;
        }

        private int bufferedOutBytesCount()
        {
            int res;
            Monitor.Enter(this.lock_output);
            res = this.bufferedOutBytes;
            Monitor.Exit(this.lock_output);
            return res;
        }

        private void repackOutputIntoChunks(byte[] data)
        {
            

            
            int size = 0;
            //Console.WriteLine(String.Format("Entering Monitor: repack, thread {0}", Thread.CurrentThread.ManagedThreadId));
            Monitor.Enter(this.lock_output);

            base.write(data); // us parent class write to enque data
            this.bufferedOutBytes += data.Length;

            while (this.out_queue.Count > 0)
            {
                //while chunksize not reached, pop to accumulation buffer
                while (size < CHUNK_SIZE && this.out_queue.Count > 0)
                {
                    byte[] bytes = (byte[])this.out_queue.Dequeue();
                    if (bytes.Length == CHUNK_SIZE && size == 0)
                    {
                        this.out_queue_backup.Enqueue(bytes);
                        continue;
                    }
                    size += bytes.Length;
                    this.accumulation_out.AddRange(bytes);
                }

                while (size >= CHUNK_SIZE)
                {
                    byte[] chunk = (this.accumulation_out.GetRange(0, CHUNK_SIZE)).ToArray();
                    this.accumulation_out.RemoveRange(0, CHUNK_SIZE);
                    size -= CHUNK_SIZE;
                    this.out_queue_backup.Enqueue(chunk);
                }

                //transfer incomplete chunk to backup queue, if last data
                if (this.out_queue.Count == 0 && size > 0)
                {
                    //add chunk without modifiing
                    byte[] chunk = (this.accumulation_out.GetRange(0, size)).ToArray();
                    this.accumulation_out.RemoveRange(0, size);
                    size = 0;
                    this.out_queue_backup.Enqueue(chunk);
                }
                
            }

            //switch out queue with backup queue
            Queue tmp = this.out_queue;
            this.out_queue = this.out_queue_backup;
            this.out_queue_backup = tmp;

            

            Monitor.Exit(this.lock_output);
            //Console.WriteLine("Exitting Monitor: repack");
        }

        public override void write(byte[] data)
        {
            repackOutputIntoChunks(data);
            //Inform about needed output processing
            if (this.out_queue.Count > 0) this.onOutDirty();
        }

        
        public override bool hasPendingOutData()
        {
            //Console.WriteLine(String.Format("Entering Monitor: hasPendingOutData, thread {0}", Thread.CurrentThread.ManagedThreadId));
            Monitor.Enter(this.lock_output);
            bool res = base.hasPendingOutData();
            Monitor.Exit(this.lock_output);
            //Console.WriteLine("Exit Monitor: hasPendingOutData");
            return res;
        }

        public override void EnqueueInput(byte[] data)
        {
            //enqueue data in IN or BIDIRECTIONAL (input) channel from processing layer
            if (this.type == Types.OUT) return; // can not enqueue data in OUT channel for processing

            //instead of enqueueing data, we pass it to the stream
            if (!this.process.HasExited)
            {
                this.stream.Write(data, 0, data.Length);
                this.stream.Flush();
            }
            //this.in_queue.Enqueue(data);
        }
    }
}
