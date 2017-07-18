using System.IO;

namespace P4wnP1
{
    /*
     * The method signature and naming of P4wnP1.Runner.run(FileStream, FileStream) mustn't be changed.
     * The method is called from stage1 and thus is the entry to stage2
     */

    public class Runner
    {
        public static void run(FileStream HIDin, FileStream HIDout)
        {
            LinkLayer ll = new LinkLayer(HIDin, HIDout);
            ll.connect();
            ll.start();

            TransportLayer tl = new TransportLayer(ll);

            Client client = new Client(tl);
            client.SendControlMessage(Client.CTRL_MSG_FROM_CLIENT_STAGE2_RUNNING); // enqueue STAGE2_RUNNING message
            client.run();
        }
    }
}
