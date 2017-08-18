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
        private bool _closeRequestedForRemote = false;
        private bool _closeRequestedForLocal = false;
        private bool _isClosed = false;


        public bool shouldBeClosed
        {
            get
            {
                return _shouldBeClosed;
            }
            set
            {
                _shouldBeClosed = value;
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

        public bool CloseRequestedForRemote
        {
            get
            {
                return _closeRequestedForRemote;
            }

            set
            {
                _closeRequestedForRemote = value;
            }
        }

        public bool CloseRequestedForLocal
        {
            get
            {
                return _closeRequestedForLocal;
            }

            set
            {
                _closeRequestedForLocal = value;
                // if this is an output channel, inform Callback listeners about the change, as processing could be needed
                if (this._closeRequestedForLocal) this.onChannelDirty?.Invoke();
            }
        }

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
            this._isClosed = true;
        }
    }

    public class StreamChannel : Channel
    {
        private Stream stream;

        
        bool passthrough;
        int passthrough_limit;
        private Thread thread_out = null;
        private ManualResetEvent passtrough_limit_not_reached = null;
        int outbuf_size = 0; //to monitor size of output queue in bytes

        byte[] readbuf = null;

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
            CHANNEL_CONTROL_REQUEST_SEEK = 9,
            CHANNEL_CONTROL_REQUEST_WRITE = 10,

            CHANNEL_CONTROL_INFORM_REMOTEBUFFER_LIMIT = 1001,
            CHANNEL_CONTROL_INFORM_REMOTEBUFFER_SIZE = 1002,
            CHANNEL_CONTROL_INFORM_WRITE_SUCCEEDED = 1003,
            CHANNEL_CONTROL_INFORM_READ_SUCCEEDED = 1004,
            CHANNEL_CONTROL_INFORM_WRITE_FAILED = 1005,
            CHANNEL_CONTROL_INFORM_READ_FAILED = 1006,
            CHANNEL_CONTROL_INFORM_FLUSH_SUCCEEDED = 1007,
            CHANNEL_CONTROL_INFORM_FLUSH_FAILED = 1008
        }

        public StreamChannel(Stream stream, CallbackOutputProcessingNeeded onOutDirty, CallbackChannelProcessingNeeded onChannelDirty, bool passthrough = true, int passthrough_limit = 3000) : base(Channel.Encodings.BYTEARRAY, Channel.Types.BIDIRECTIONAL, onOutDirty, onChannelDirty)
        {
            /*
             * passtrough is a design decission, working like this:
             *      - if the underlying Stream is readable, read operations are done continuous and automatically
             *      - as reading could block, this is handle by a sperate thread
             *      - data read is pushed directly to the channel output queue
             *      - if the channel queue grows to passthroug_limit, this processed is paused until
             *      the communication layer responsible for output data processing has dequeued enough
             *      data, to shrink the queue below this limit
             *      
             *      In summary this means data is continueosly sent to the client, as long as it could be read
             *      from the stream. Thus a readable StreamChannel puts load to theLinkLayer permanently (throttled
             *      by the passthrough_limit). Additionally, this means if data isn't processed, buffer grow - this
             *      happens on the other endpoint.
             *      
             *      A different approach would be to read data "on demand". This again would involve much more control
             *      communication. With NetworkStreams in mind, this seems to be the better solution '(hopefully)
             *      
             * ToDo:
             * - The channels onClose() method gets called by the client, if the channel is removed from the clients channel list.
             *   Current implementation allows the channel to close the underlying stream object. This is somehow wrong and should be initiated
             *   ba a request of the remote peer (close_stream_request). In order to allow the remote peer to send such a request, it
             *   has to be aware of the fact that the channel has been closed. This isn't the case at the moment, as the peer doesn't get informed about this.
             * - As there's no communication to the peer, when a channel gets closed, pending channel objects reside on the other endpoit when removed
             *   from this peer.
             * - as this isn't read on demand, a method has to be found to signal the remote peer if EOF is reached on reading. This could for example
             *   be done by writing an empty stream to the channel output queue (empty payload, the byte signaling that this is data has to be included)
             */



            this.stream = stream;
            this.passthrough = passthrough;
            this.passthrough_limit = passthrough_limit;

            //if we have a readable stream and passtrough is enabled, we create a thread to passthrough data read
            if (stream.CanRead && this.passthrough)
            {
                this.passtrough_limit_not_reached = new ManualResetEvent(true);
                this.thread_out = new Thread(new ThreadStart(this.passThruOut));
                thread_out.Start();
            }
            else if (stream.CanRead && !this.passthrough)
            {
                // prepare read buffer
                this.readbuf = new byte[60000];
            }

            Console.WriteLine(String.Format("StreamChannel created {0}, passthrough: {1}", this.ID, passthrough));
        }

        public void passThruOut()
        {
            int READ_BUFFER_SIZE = this.passthrough_limit;
            byte[] readBuf = new byte[READ_BUFFER_SIZE];
            List<byte> readbufCopy = new List<byte>();


            int count = -1;

            while (!this.shouldBeClosed && this.stream.CanRead && count != 0)
            {
                this.passtrough_limit_not_reached.WaitOne();

                count = this.stream.Read(readBuf, 0, readBuf.Length); //this doesn't block
                
                // trim data down to count
                readbufCopy.AddRange(readBuf);
                //readbufCopy.GetRange(0, count);
                readbufCopy.RemoveRange(count, READ_BUFFER_SIZE - count);
                
                this.writeData(readbufCopy);

                Console.WriteLine(String.Format("Data passed to StreamChannel out: {0}", Encoding.UTF8.GetString(readbufCopy.ToArray())));

                readbufCopy.Clear();
            }

            //if end here, this means the last read has been with count==0, this indicates an EOF
            //
            //as the server receives this information (if read count==0 an empty stream is sent to the peer, which corresponds to an EOF)
            //it is up to the server to decide to close the chanel

        }

        private void writeData(List<byte> data)
        {
            List<byte> new_data = Struct.packByte((byte)0);
            new_data.AddRange(data);
            this.outbuf_size += data.Count;
            if (this.passthrough && (this.outbuf_size >= this.passthrough_limit)) this.passtrough_limit_not_reached.Reset();
            base.write(new_data.ToArray());
        }

        public override byte[] DequeueOutput()
        {
            byte[] ret_data = base.DequeueOutput();
            this.outbuf_size -= ret_data.Length;
            if (this.passthrough && (this.outbuf_size < this.passthrough_limit)) this.passtrough_limit_not_reached.Set();
            return ret_data;
        }

        private void writeControlMessage(List<byte> data)
        {
            List<byte> new_data = Struct.packByte((byte)1);
            new_data.AddRange(data);
            base.write(new_data.ToArray());
        }

        public override void write(byte[] data)
        {
            this.writeData(new List<byte>(data));
        }

        public override void onClose()
        {
            base.onClose();

            if (this.thread_out != null) this.thread_out.Abort();

            /*
            //ToDo: Stream closing shouldn't be handled here, as the stream is still referenced by he client
            try
            {
                stream.Flush();
                stream.Dispose();
                stream.Close();
            }
            catch (ObjectDisposedException e)
            {
                Console.WriteLine("Stream has already been disposed when trying to close");
            }
            */
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
                case CH_MSG_TYPE.CHANNEL_CONTROL_REQUEST_FLUSH:
                    Console.WriteLine(String.Format("Received FLUSH request for StreamChannel {0}", this.ID));
                    stream.Flush();

                    //return message, stating that everything has been written (should be handed by output thread)
                    List<byte> flush_response = Struct.packUInt32((UInt32)StreamChannel.CH_MSG_TYPE.CHANNEL_CONTROL_INFORM_FLUSH_SUCCEEDED);
                    this.writeControlMessage(flush_response);

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
                case CH_MSG_TYPE.CHANNEL_CONTROL_REQUEST_WRITE:
                    int size = Struct.extractInt32(msg);
                    byte[] data_to_write = msg.ToArray();

                    //the data shouldn't be written from this thread, so the operation has to be moved to processing thread
                    this.stream.Write(data_to_write, 0, data_to_write.Length);

                    //return message, stating that everything has been written (should be handed by output thread)
                    List<byte> write_response = Struct.packUInt32((UInt32) StreamChannel.CH_MSG_TYPE.CHANNEL_CONTROL_INFORM_WRITE_SUCCEEDED);
                    write_response = Struct.packInt32(data_to_write.Length, write_response);
                    this.writeControlMessage(write_response);

                    break;
                case CH_MSG_TYPE.CHANNEL_CONTROL_REQUEST_READ:
                    int count = Struct.extractInt32(msg);
                    int timeout = Struct.extractInt32(msg);

                    Console.WriteLine(String.Format("Received READ request for StreamChannel {0}, count {1}, timeout {2}", this.ID, count, timeout));


                    //the data shouldn't be readen from this thread, blocking would stop the input thread (has to be done from processing thread
                    if (this.readbuf.Length < count) this.readbuf = new byte[count]; //resize read buffer if needed
                    int read_size = this.stream.Read(this.readbuf, 0, count);
                    List<Byte> read_data = new List<byte>(this.readbuf);
                    read_data.RemoveRange(read_size, this.readbuf.Length - read_size);

                    //return message, stating legngth of read data and the data itself (should be handed by output thread)
                    List<byte> read_response = Struct.packUInt32((UInt32)StreamChannel.CH_MSG_TYPE.CHANNEL_CONTROL_INFORM_READ_SUCCEEDED);
                    read_response = Struct.packInt32(read_size, read_response);
                    read_response.AddRange(read_data);
                    this.writeControlMessage(read_response);
                    
                    break;
                default:
                    Console.WriteLine(String.Format("Received unknown channel message for StreamChannel {0}", this.ID));
                    break;
            }
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
