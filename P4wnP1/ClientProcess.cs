using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace P4wnP1
{
    public class ClientProcess
    {
        //private UInt32 id;
        //private string filename;
        //private string args;
        private bool useChannels;
        private ProcessChannel ch_stdin = null, ch_stderr = null, ch_stdout = null;
        private Process process;
        private ProcessStartInfo processStartInfo;

        public delegate void onExitCallback(ClientProcess cproc);

        private onExitCallback exitCallbacks = null;

        public ClientProcess(string filename, string args, bool useChannels, Channel.CallbackOutputProcessingNeeded onOutDirty)
        {
            //this.filename = filename;
            //this.args = args;
            this.useChannels = useChannels;

            this.processStartInfo = new ProcessStartInfo(filename, args);

            this.processStartInfo.CreateNoWindow = false;

            if (useChannels)
            {
                this.processStartInfo.UseShellExecute = false;
                this.processStartInfo.RedirectStandardInput = true;
                this.processStartInfo.RedirectStandardOutput = true;
                this.processStartInfo.RedirectStandardError = true;
            }

            this.process = new Process();
            this.process.StartInfo = this.processStartInfo;
            this.process.Start();

            if (useChannels)
            {
                // create Channels objects and bind to streams
                this.ch_stdin = new ProcessChannel(this.process, this.process.StandardInput.BaseStream, Channel.Encodings.UTF8, Channel.Types.IN, onOutDirty);
                this.ch_stdout = new ProcessChannel(this.process, this.process.StandardOutput.BaseStream, Channel.Encodings.UTF8, Channel.Types.OUT, onOutDirty);
                this.ch_stderr = new ProcessChannel(this.process, this.process.StandardError.BaseStream, Channel.Encodings.UTF8, Channel.Types.OUT, onOutDirty);

                this.ch_stdout.registerProcExitedCallback(onExit);
            }

            
        }

        public void registerOnExitCallback(onExitCallback callback)
        {
            this.exitCallbacks += callback;
        }

        private void onExit(Process process)
        {
            if (this.exitCallbacks != null) this.exitCallbacks(this);
        }

        public string proc_filename
        {
            get { return this.processStartInfo.FileName; }
        }

        public string proc_args
        {
            get { return this.processStartInfo.Arguments; }
        }

        public Channel Ch_stdin
        {
            get
            {
                return ch_stdin;
            }
        }

        public Channel Ch_stderr
        {
            get
            {
                return ch_stderr;
            }

        }

        public Channel Ch_stdout
        {
            get
            {
                return ch_stdout;
            }
        }

        public int Id
        {
            get
            {
                return this.process.Id;
                
            }
        }

        public void kill()
        {
            this.process.Kill();
        }
    }
}
