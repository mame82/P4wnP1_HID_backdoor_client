using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace P4wnP1
{
    [Serializable]
    public class ClientMethodException : Exception
    {
        public ClientMethodException(String message) : base(message)
        { }
    }

    public class ClientMethod
    {
        public UInt32 id { get; }
        public String name { get; }
        public bool started { get; }
        public bool finished { get; set; }
        public byte[] args { get; }
        public byte[] result { get; set; }
        public bool error { get; set; }
        public String error_message { get; set; }

        public ClientMethod(UInt32 id, String name, byte[] args)
        {
            this.id = id;
            this.name = name;
            this.args = args;
            this.finished = false;
            this.error = false;
            this.started = false;
        }

        public ClientMethod(List<byte> data)
        {
            // generate client object based on data received in a run_method control message

            this.id = Struct.extractUInt32(data);
            this.name = Struct.extractNullTerminatedString(data);
            this.args = data.ToArray();
            this.finished = false;
            this.error = false;
            this.started = false;
        }

        public void setError(String errMsg)
        {
            this.error = true;
            this.error_message = errMsg;
            this.finished = true;

        }

        public void setResult(byte[] result)
        {
            if (result == null)
            {
                this.setError(String.Format("Method {0} was called, but returned nothing", this.name));
                return;
            }
            this.result = result;
            this.finished = true;
        }

        public byte[] createResponse()
        {
            // this function should only be called when the method has finished (finished member == $true), but anyway, this isn't checked here

            // first response field is uint32 method id
            List<byte> response = Struct.packUInt32(this.id);

            // next field is a ubyte indicating success or error (0 success, everything else error)
            if (this.error)
            {
                response = Struct.packByte((byte)1, response); // indicating an error
                response = Struct.packNullTerminatedString(this.error_message, response); //append error message
                return response.ToArray(); // hand back error response
            }

            response = Struct.packByte((byte)0, response); // add success field
            response = Struct.packByteArray(this.result, response);

            return response.ToArray(); // return result

            
        }
    }
}
