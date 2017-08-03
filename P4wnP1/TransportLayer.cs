using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;

namespace P4wnP1
{
    public class TransportLayer
    {
        private LinkLayer ll;
        private Hashtable inChannels;
        private Hashtable outChannels;
        private List<Channel> channelsToRemove;
        private Channel control_channel;
        //private UInt32 next_channel_id;
        private AutoResetEvent eventChannelOutputNeedsToBeProcessed;
        private bool running;
        private object lockChannels;

        public TransportLayer(LinkLayer linklayer)
        {
            this.lockChannels = new object();
            this.eventChannelOutputNeedsToBeProcessed = new AutoResetEvent(true);
            this.ll = linklayer;
            this.inChannels = Hashtable.Synchronized(new Hashtable());
            this.outChannels = Hashtable.Synchronized(new Hashtable());
            this.channelsToRemove = new List<Channel>();
            this.control_channel = this.CreateAndAddChannel(Channel.Types.BIDIRECTIONAL, Channel.Encodings.BYTEARRAY, this.setOutputProcessingNeeded, null); //Caution, this has to be the first channel to be created, in order to assure channel ID is 0

            this.running = true;
            
            //new Channel(Channel.Encodings.BYTEARRAY, Channel.Types.BIDIRECTIONAL);
            //this.inChannels.Add(this.control_channel.ID, this.control_channel);
            //this.outChannels.Add(this.control_channel.ID, this.control_channel);

            //this.next_channel_id = 1;
        }

        public void registerTimeoutCallback(LinkLayer.LinkLayerTimeoutCallback callback)
        {
            this.ll.registerTimeoutCallback(callback);
        }

        public void WriteControlChannel(byte[] data)
        {
            this.control_channel.write(data);
        }

        public void waitForData()
        {
            this.ll.WaitForInputStream();
        }

        public void setOutputProcessingNeeded()
        {
            this.eventChannelOutputNeedsToBeProcessed.Set();
        }

        public void stop()
        {
            this.running = false;
            this.ll.stop();
        }

        //input and output of channels is processed in two dedicated Client threads
        //if the channel needs to do time consuming operations, this could be done here (and thus by the
        //processing thread of the client)
        public void ProcessChannels()
        {
            //check for closed channels
            Monitor.Enter(this.lockChannels);
            ICollection keys = this.outChannels.Keys;
            foreach (Object key in keys)
            {
                Channel channel = (Channel)this.outChannels[key];
                if (channel.shouldBeClosed) this.channelsToRemove.Add(channel);
                //processing for out channel in else branch
            }
            keys = this.inChannels.Keys;
            foreach (Object key in keys)
            {
                Channel channel = (Channel)this.inChannels[key];
                if (channel.shouldBeClosed) this.channelsToRemove.Add(channel);
                //processing for in channel in else branch
            }

            //remove closed channels
            foreach (Channel channel in this.channelsToRemove)
            {
                if (this.inChannels.Contains(channel.ID)) this.inChannels.Remove(channel.ID);
                if (this.outChannels.Contains(channel.ID)) this.outChannels.Remove(channel.ID);
                channel.onClose();
            }

            
            Monitor.Exit(this.lockChannels);

            
        }

        public void ProcessOutSingle(bool blockIfNotthingToProcess)
        {
            if (blockIfNotthingToProcess)
            {
                //stop processing until sgnal is received
                while (true)
                {
                    if (this.eventChannelOutputNeedsToBeProcessed.WaitOne(100) || !this.running) break;
                }
            }

            //abort if we aren't in run state anymore
            if (!this.running) return;

            Monitor.Enter(this.lockChannels);
            ICollection keys = this.outChannels.Keys;

            Console.WriteLine(String.Format("Out channel count {0}", keys.Count));

            foreach (Object key in keys)
            {
                Channel channel = (Channel) this.outChannels[key];

                //while (channel.hasPendingOutData())
                if (channel.hasPendingOutData()) //we only process a single chunk per channel (load balancing) and we only deliver data if the channel is linked 
                {
                    UInt32 ch_id = (UInt32) channel.ID;

                    if ((ch_id == 0) || channel.isLinked) // send output only if channel is linked (P4wnP1 knows about it) or it is the control channel (id 0)
                    {
                        

                        byte[] data = channel.DequeueOutput();

                        List<byte> stream = Struct.packUInt32(ch_id);
                        stream = Struct.packByteArray(data, stream);

                        //Console.WriteLine("TransportLayer: trying to push channel data");

                        if (ch_id == 0) this.ll.PushOutputStreamNoBlock(stream.ToArray());
                        else this.ll.PushOutputStream(stream.ToArray());
                    }
                }

                if (channel.hasPendingOutData()) this.eventChannelOutputNeedsToBeProcessed.Set(); //reenable event, if there's still data to process
            }
            Monitor.Exit(this.lockChannels);
            //We only reenable the signal if there's still data to process


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
            Monitor.Enter(this.lockChannels);
            if (this.inChannels.Contains(id))
            {
                Monitor.Exit(this.lockChannels);
                return (Channel)this.inChannels[id];
            }
            if (this.outChannels.Contains(id))
            {
                Monitor.Exit(this.lockChannels);
                return (Channel)this.outChannels[id];
            }
            Monitor.Exit(this.lockChannels);

            return null;
        }

        
        public Channel CreateAndAddChannel(Channel.Types type, Channel.Encodings encoding, Channel.CallbackOutputProcessingNeeded onOutDirty, Channel.CallbackChannelProcessingNeeded onChannelDirty)
        {
            Channel ch = new Channel(encoding, type, onOutDirty, onChannelDirty);

            this.AddChannel(ch);
            
            return ch;
        }
        

        public Channel AddChannel(Channel ch)
        {
            Monitor.Enter(this.lockChannels);
            if (ch.type != Channel.Types.OUT) this.inChannels.Add(ch.ID, ch);
            if (ch.type != Channel.Types.IN) this.outChannels.Add(ch.ID, ch);
            Monitor.Exit(this.lockChannels);
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
