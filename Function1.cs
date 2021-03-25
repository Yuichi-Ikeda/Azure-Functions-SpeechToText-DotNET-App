using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Extensions.Logging;
using Azure.Storage.Blobs;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;

namespace Azure_Functions_SpeechToText_DotNET_App
{
    public static class Function1
    {
        /** 
        * .wav ファイルが audio コンテナにアップロードされると実行
        * https://docs.microsoft.com/ja-jp/azure/storage/blobs/storage-quickstart-blobs-dotnet
        */
        [FunctionName("audio2text")]
        public static void Run([BlobTrigger("audio/{name}.wav", Connection = "AudioStorage")]Stream myBlob, string name, ILogger log)
        {
            log.LogInformation($"C# Blob trigger function Processed blob\n Name:{name} \n Size: {myBlob.Length} Bytes");

            // 環境変数から値を取得
            string tempfile = System.Environment.GetEnvironmentVariable("TMP") + "\\" + name;
            string connectStr = System.Environment.GetEnvironmentVariable("AudioStorage");

            // Step 1. Blob から audio ファイルをダウンロード
            BlobServiceClient blobServiceClient = new BlobServiceClient(connectStr);
            BlobContainerClient containerClient = blobServiceClient.GetBlobContainerClient("audio");
            BlobClient blobClient = containerClient.GetBlobClient(name + ".wav");
            using (FileStream downloadFileStream = File.OpenWrite(tempfile + ".wav")) {
                blobClient.DownloadTo(downloadFileStream);
                downloadFileStream.Close();
            }

            // Step 2. audio ファイルから文字起こし
            Audio2Text(tempfile, log);

            // Step 3. 抽出したテキストファイルを Blob へ格納
            containerClient = blobServiceClient.GetBlobContainerClient("text");
            blobClient = containerClient.GetBlobClient(name + ".txt");
            using (FileStream uploadFileStream = File.OpenRead(tempfile + ".txt")) {
                blobClient.Upload(uploadFileStream, true);
                uploadFileStream.Close();
            }

            // 一時ファイルの削除
            File.Delete(tempfile + ".wav");
            File.Delete(tempfile + ".txt");
        }

        /**
         * Speech サービスで audio ファイルから文字起こし
         * https://docs.microsoft.com/ja-jp/azure/cognitive-services/speech-service/get-started-speech-to-text
         */
        private static void Audio2Text(string tempfile, ILogger log)
        {
            FileStream fs;
            try {
                fs = File.OpenWrite(tempfile + ".txt");
            } catch(Exception ex) {
                log.LogWarning(ex.Message);
                return;
            }

            // Speech サービスへ接続
            string key = System.Environment.GetEnvironmentVariable("CognitiveServiceApiKey");
            string endPoint = System.Environment.GetEnvironmentVariable("CognitiveEndpoint");
            Uri uriEndpoint;
            try {
                uriEndpoint = new Uri(endPoint);
            }
            catch (Exception ex) {
                log.LogWarning(ex.Message);
                return;
            }

            SpeechConfig speechConfig = SpeechConfig.FromEndpoint(uriEndpoint, key);
            speechConfig.SpeechRecognitionLanguage = "ja-JP";
            AudioConfig audioConfig = AudioConfig.FromWavFileInput(tempfile + ".wav");
            SpeechRecognizer recognizer = new SpeechRecognizer(speechConfig, audioConfig);

            // イベントタスクの同期用
            var stopRecognition = new TaskCompletionSource<int>();

            // 部分文字列の抽出毎に繰り返し呼ばれる
            recognizer.Recognized += (s, e) =>
            {
                if (e.Result.Reason == ResultReason.RecognizedSpeech) {
                    String transcript = e.Result.Text;
                    log.LogInformation("RECOGNIZED: Text=" + transcript);
                    try {
                        Byte[] info = new UTF8Encoding(true).GetBytes(transcript);
                        fs.Write(info, 0, info.Length);
                    }
                    catch (Exception ex) {
                        log.LogWarning(ex.Message);
                    }
                }
                else if (e.Result.Reason == ResultReason.NoMatch) {
                    log.LogInformation($"NOMATCH: Speech could not be recognized.");
                }
            };

            // 途中で処理が完了したら呼ばれる
            recognizer.Canceled += (s, e) =>
            {
                Console.WriteLine($"CANCELED: Reason={e.Reason}");

                if (e.Reason == CancellationReason.Error)
                {
                    log.LogInformation($"CANCELED: ErrorCode={e.ErrorCode}");
                    log.LogInformation($"CANCELED: ErrorDetails={e.ErrorDetails}");
                    log.LogInformation($"CANCELED: Did you update the subscription info?");
                }

                stopRecognition.TrySetResult(0);
            };

            // 最後まで完了したら呼ばれる
            recognizer.SessionStopped += (s, e) =>
            {
                log.LogInformation("\n    Session stopped event.");
                stopRecognition.TrySetResult(0);
            };

            // 文字起こしの開始
            recognizer.StartContinuousRecognitionAsync();

            // Waits for completion. Use Task.WaitAny to keep the task rooted.
            Task.WaitAny(new[] { stopRecognition.Task });

            recognizer.Dispose();
            audioConfig.Dispose();
            fs.Close();
        }
    }
}
