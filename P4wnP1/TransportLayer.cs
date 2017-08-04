using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;

namespace P4wnP1
{
    public class TransportLayer
    {
        private LinkLayer ll;
        
        //private UInt32 next_channel_id;
        private bool running;
        

        public TransportLayer(LinkLayer linklayer)
        {
            
            
            this.ll = linklayer;
            

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

        public void writeOutputStream(byte[] stream, bool blockOnLimit = true)
        {
            if (blockOnLimit) this.ll.PushOutputStream(stream);
            else this.ll.PushOutputStreamNoBlock(stream);
        }

        public byte[] readInputStream()
        {
            return this.ll.PopPendingInputStream();
        }

        public void waitForData()
        {
            this.ll.WaitForInputStream();
        }

        public bool hasData()
        {
            return (this.ll.PendingInputStreamCount() > 0);
        }



        public void stop()
        {
            this.running = false;
            this.ll.stop();
        }

        

        


    }
}
