using System;
using System.Collections;
using System.Collections.Generic;

namespace P4wnP1
{
    public class TransportLayer
    {
        private LinkLayer ll;
        private Hashtable inChannels;
        private Hashtable outChannels;
        private Channel control_channel;
        //private UInt32 next_channel_id;

        public TransportLayer(LinkLayer linklayer)
        {
            this.ll = linklayer;
            this.inChannels = Hashtable.Synchronized(new Hashtable());
            this.outChannels = Hashtable.Synchronized(new Hashtable());
            this.control_channel = this.CreateChannel(Channel.Types.BIDIRECTIONAL, Channel.Encodings.BYTEARRAY); //Caution, this has to be the first channel to be created, in order to assure channel ID is 0
                
            //new Channel(Channel.Encodings.BYTEARRAY, Channel.Types.BIDIRECTIONAL);
            //this.inChannels.Add(this.control_channel.ID, this.control_channel);
            //this.outChannels.Add(this.control_channel.ID, this.control_channel);

            //this.next_channel_id = 1;
        }

        public void WriteControlChannel(byte[] data)
        {
            this.control_channel.write(data);
        }

        public void waitForData()
        {
            this.ll.WaitForInputStream();
        }

        
        public void ProcessOutSingle()
        {
            //System.Diagnostics.Stopwatch sw = new System.Diagnostics.Stopwatch();
            
            ICollection keys = this.outChannels.Keys;
            foreach (Object key in keys)
            {
                Channel channel = (Channel) this.outChannels[key];

                //while (channel.hasPendingOutData())
                if (channel.hasPendingOutData()) //we only process a single chunk per channel (load balancing)
                {
                    UInt32 ch_id = (UInt32) channel.ID;
                    byte[] data = channel.DequeueOutput();

                    List<byte> stream = Struct.packUInt32(ch_id);
                    stream = Struct.packByteArray(data, stream);

                    //Console.WriteLine("TransportLayer: trying to push channel data");
                    //sw.Start();
                    this.ll.PushOutputStream(stream.ToArray());
                    //sw.Stop();
            //        Console.WriteLine(sw.ElapsedMilliseconds);
                    //sw.Reset();
                    //Console.WriteLine("TransportLayer: done pushing channel data");
                }
            }

            /*
            if (this.ll.PendingOutputStreamCount() > 0)
            {
                Console.WriteLine(String.Format("TransportLayer outstream count: {0}, bytesize: {1}", this.ll.PendingOutputStreamCount(), this.ll.OutputQueueByteSize));
            }
            */
        }

        public void ProcessInSingle(bool blockIfNoData)
        {
            if (blockIfNoData) this.waitForData();


            while (this.ll.PendingInputStreamCount() > 0) //as long as linklayer has data
            {
                List<byte> stream = new List<byte>(this.ll.PopPendingInputStream());
                UInt32 ch_id = Struct.extractUInt32(stream);

                byte[] data = stream.ToArray(); //the extract method removed the first elements from the List<byte> and we convert back to an array now

                Channel target_ch = this.GetChannel(ch_id);
                if (target_ch == null)
                {
                    Console.WriteLine(String.Format("Received data for channel with ID {0}, this channel doesn't exist!", ch_id));
                    continue;
                }

                target_ch.EnqueueInput(data);
            }
        }


        public Channel GetChannel(UInt32 id)
        {
            //get channel by ID, return null if not found
            
            if (this.inChannels.Contains(id)) return (Channel)this.inChannels[id];
            if (this.outChannels.Contains(id)) return (Channel)this.outChannels[id];

            return null;
        }

        public Channel CreateChannel(Channel.Types type, Channel.Encodings encoding)
        {
            Channel ch = new Channel(encoding, type);
            

            if (ch.type != Channel.Types.OUT) this.inChannels.Add(ch.ID, ch);
            if (ch.type != Channel.Types.IN) this.outChannels.Add(ch.ID, ch);

            return ch;
        }

        public Channel AddChannel(Channel ch)
        {
            if (ch.type != Channel.Types.OUT) this.inChannels.Add(ch.ID, ch);
            if (ch.type != Channel.Types.IN) this.outChannels.Add(ch.ID, ch);

            return ch;
        }
        /*
        public Channel GetChannel(UInt32 id)
        {
            //get channel by ID, return null if not found
            string id_str = id.ToString();
            if (this.inChannels.Contains(id_str)) return (Channel) this.inChannels[id_str];
            if (this.outChannels.Contains(id_str)) return (Channel) this.outChannels[id_str];

            return null;
        }
        */
    }
}
