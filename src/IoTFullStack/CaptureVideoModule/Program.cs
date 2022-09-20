using FFMpegCore;
using Microsoft.Azure.Devices.Client;
using Microsoft.Azure.Devices.Client.Transport.Mqtt;
using RtspClientSharp;
using RtspClientSharp.Rtsp;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.Loader;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;

namespace CaptureVideoModule
{
    internal class Program
    {
        static int counter;
        static bool CaptureVideo { get; set; } = Environment.GetEnvironmentVariable("RTSP_CAPTURE_VIDEO") != null ?
                                                 Convert.ToBoolean(Environment.GetEnvironmentVariable("RTSP_CAPTURE_VIDEO")) : true;
        static string CaptureVideoUrl { get; set; } = Environment.GetEnvironmentVariable("RTSP_HOST") != null ?
                                                     $"rtsp://{Environment.GetEnvironmentVariable("RTSP_HOST")}:{Environment.GetEnvironmentVariable("RTSP_PORT")}/{Environment.GetEnvironmentVariable("RTSP_PATH")}" :
                                                     //"rtsp://localhost:8554/mystream";
                                                     "rtsp://192.168.0.33:8554/raw";
        static string CaptureVideoOutputFolder { get; set; } = Environment.GetEnvironmentVariable("RTSP_STORAGE_FOLDER_CAPTURE_VIDEO") ?? @"c:\tmp\capture";
        static int CaptureVideoThreshold { get; set; } = Environment.GetEnvironmentVariable("RTSP_THRESHOLD") != null ?
                                                        Convert.ToInt32(Environment.GetEnvironmentVariable("RTSP_THRESHOLD")) : 2;
        static string DeviceId { get; set; } = Environment.GetEnvironmentVariable("DEVICE_ID") ?? "test";
        static string EdgeConnStr { get; set; } = Environment.GetEnvironmentVariable("EDGE_CONN_STR") ?? $"HostName=iot-hub-netcoreconf-2022.azure-devices.net;DeviceId={DeviceId};SharedAccessKey=msM832UP1Cv65Al+0aG5NoS7DHmioJWru2V/u1qV9vQ=";
        

        static void Main(string[] args)
        {
            Init().Wait();

            // Wait until the app unloads or is cancelled
            var cts = new CancellationTokenSource();
            AssemblyLoadContext.Default.Unloading += (ctx) => cts.Cancel();
            Console.CancelKeyPress += (sender, cpe) => cts.Cancel();
            WhenCancelled(cts.Token).Wait();
        }

        /// <summary>
        /// Handles cleanup operations when app is cancelled or unloads
        /// </summary>
        public static Task WhenCancelled(CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<bool>();
            cancellationToken.Register(s => ((TaskCompletionSource<bool>)s).SetResult(true), tcs);
            return tcs.Task;
        }

        /// <summary>
        /// Initializes the ModuleClient and sets up the callback to receive
        /// messages containing temperature information
        /// </summary>
        private static async Task Init()
        {
            MqttTransportSettings mqttSetting = new MqttTransportSettings(TransportType.Mqtt_Tcp_Only);
            ITransportSettings[] settings = { mqttSetting };

            // Open a connection to the Edge runtime
            ModuleClient ioTHubModuleClient = await ModuleClient.CreateFromEnvironmentAsync(settings);
            await ioTHubModuleClient.OpenAsync();
            Console.WriteLine("IoT Hub module client initialized.");

            DeviceClient ioTHubDeviceClient = DeviceClient.CreateFromConnectionString(EdgeConnStr);
            
            Console.WriteLine("IoT Hub device client initialized.");

            // Register callback to be called when a message is received by the module
            Console.WriteLine($"Creating storage folder {CaptureVideoOutputFolder}");
            Directory.CreateDirectory(CaptureVideoOutputFolder);
            Console.WriteLine($"Storage folder created");

            if (CaptureVideo)
            {
                CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
                CancellationToken token = cancellationTokenSource.Token;
             
                await CaptureVideoAsync(ioTHubModuleClient, ioTHubDeviceClient, cancellationTokenSource);
                await UploadFileAsync(ioTHubDeviceClient); 
            }
            else
            {
                Console.WriteLine("No capturing video by configuration!");
            }
            
        }

        /// <summary>
        /// This method is called whenever the module is sent a message from the EdgeHub. 
        /// It just pipe the messages without any change.
        /// It prints all the incoming messages.
        /// </summary>
        private static async Task CaptureVideoAsync(ModuleClient userContextModuleClient, DeviceClient userContextDeviceClient, CancellationTokenSource cancellationTokenSource)
        {
            int counterValue = Interlocked.Increment(ref counter);

            if (!(userContextModuleClient is ModuleClient moduleClient))
            {
                throw new InvalidOperationException("userContextModuleClient doesn't contain " + "expected values");
            }

            if (!(userContextDeviceClient is DeviceClient deviceClient))
            {
                throw new InvalidOperationException("userContextDeviceClient doesn't contain " + "expected values");
            }
             
            TimeSpan retryDelay = TimeSpan.FromSeconds(1);
            var recordStart = DateTime.UtcNow;
            var serverUri = new Uri(CaptureVideoUrl);
            var credentials = new NetworkCredential("", "");
            var connectionParameters = new ConnectionParameters(serverUri, credentials)
            {
                RtpTransport = RtpTransportProtocol.UDP
            };

            Console.WriteLine($"Capturing from {CaptureVideoUrl}");

            TimeSpan delay = TimeSpan.FromSeconds(1);

            using var rtspClient = new RtspClient(connectionParameters);
                            

            var mp4FilePath = Path.Combine(CaptureVideoOutputFolder, $"test{DateTime.UtcNow.Ticks}.mp4");
            var recordFile = File.Create(mp4FilePath);
            rtspClient.FrameReceived += (sender, rawFrame) =>
            {
                DateTime now = DateTime.UtcNow;
                if (now > recordStart.AddSeconds(CaptureVideoThreshold))
                {
                    cancellationTokenSource.Cancel();
                }
                if (cancellationTokenSource == null || cancellationTokenSource.IsCancellationRequested)
                {
                    return;
                }

                string snapshotName = rawFrame.Timestamp.ToString("O").Replace(":", "_") + ".png";
                string path = Path.Combine(CaptureVideoOutputFolder, snapshotName);             

                ArraySegment<byte> frameSegment = rawFrame.FrameSegment;

                using var stream = File.OpenWrite(path);
                stream.Write(frameSegment.Array, frameSegment.Offset, frameSegment.Count);

                Console.WriteLine($"[{DateTime.UtcNow}] Snapshot is saved to {snapshotName}");
            };

            while (true)
            {
                Console.WriteLine("Connecting...");
                try
                {
                    await rtspClient.ConnectAsync(cancellationTokenSource.Token);
                }
                catch (InvalidCredentialException)
                {
                    await Task.Delay(retryDelay, cancellationTokenSource.Token);
                    continue;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (RtspClientException e)
                {
                    await Task.Delay(retryDelay, cancellationTokenSource.Token);
                }

                Console.WriteLine("Connected");

                Console.WriteLine("Receiving frames...");
                try
                {
                    await rtspClient.ReceiveAsync(cancellationTokenSource.Token);                    
                }
                catch (TaskCanceledException e)
                {
                    
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (RtspClientException e)
                {
                    Console.WriteLine(e.ToString());
                    await Task.Delay(retryDelay, cancellationTokenSource.Token);
                }
            }
            
        }

        private static async Task UploadFileAsync(DeviceClient deviceClient)
        {
            Console.WriteLine("Processing frames saved.");
            //GlobalFFOptions.Configure(new FFOptions { BinaryFolder = @"C:\ffmpeg\bin" });
            //var mp4FilePath = Path.Combine(CaptureVideoOutputFolder, $"test{DateTime.UtcNow.Ticks}.mp4");

            //var pathFiles = Directory.EnumerateFiles(CaptureVideoOutputFolder);
            //ImageInfo[] images = new ImageInfo[pathFiles.Count()];

            //for (var index = 0; index < pathFiles.Count(); index++)
            //{
            //    var path = pathFiles.ElementAt(index);
            //    images[index] = ImageInfo.FromPath(path);
            //}

            //FFMpeg.JoinImageSequence(mp4FilePath, frameRate: 1, images);
            //var sample = new FileUpload(deviceClient);
            //await sample.UploadFileAsync(mp4FilePath);

            var pathFiles = Directory.EnumerateFiles(CaptureVideoOutputFolder);
            var sample = new FileUpload(deviceClient);
            for (var index = 0; index < pathFiles.Count(); index++)
            {
                var path = pathFiles.ElementAt(index);
                await sample.UploadFileAsync(path);
            }
        }
    }
}
