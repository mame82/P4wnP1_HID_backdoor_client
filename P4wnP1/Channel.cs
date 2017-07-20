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

        public delegate void CallbackOutputProcessingNeeded(); //the delgate is used, to inform somebody that output processing is needed (in our case the LinkLayer)

        public Channel(Encodings encoding, Types type, CallbackOutputProcessingNeeded onOutDirty)
        {
            this.ID = next_id;
            next_id++;
            this.encoding = encoding;
            this.type = type;
            this.onOutDirty = onOutDirty;
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

        public ProcessChannel(Process process, Stream stream, Encodings encoding, Types type, CallbackOutputProcessingNeeded onOutDirty) : base(encoding, type, onOutDirty)
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

            /*
            Monitor.Enter(this.lock_output);
            // if there is already out data, we accumulate it to avoid creating multiple streams for a low number of bytes, each
            if (this.out_queue.Count > 0)
            {
                while (this.out_queue.Count > 0)
                {
                    this.accumulation_out.AddRange((byte[])this.out_queue.Dequeue());
                }
                this.accumulation_out.AddRange(data);
                byte[] acc = this.accumulation_out.ToArray();
                //Console.WriteLine(String.Format("Accumulated: {0}", Encoding.UTF8.GetString(acc)));
                base.write(acc);

                this.accumulation_out.Clear();
            }
            else
            {
                //Console.WriteLine(String.Format("Not accumulated: {0}", Encoding.UTF8.GetString(data)));
                base.write(data);
            }
            Monitor.Exit(this.lock_output);
            */
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
