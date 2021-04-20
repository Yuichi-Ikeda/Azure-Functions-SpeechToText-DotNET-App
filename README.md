# 日本語音声ファイルから文字起こしをする C# アプリケーション

## 概要
　Azure Blob ストレージの audio コンテナに音声ファイルを配置すると、それをトリガーとして起動する Azure Functions C# アプリケーション。音声ファイルを Cognitive Speech サービスに渡し、 得られた日本語の音声テキストを text コンテナに保存します。

 <img src="/images/workflow.png" title="workflow">

### クラウド実行環境
- Azure Functions : Windows ベース環境
- .NET Core 3.1

　デプロイ後に Azure Functions の構成メニュー、アプリケーション設定で環境変数として local.settings.json の値を登録する必要があります。

### ローカル開発環境
- Visual Studio

　クラウド側に Azure 汎用ストレージ、Cognitive Speech サービスのデプロイが必要です。

## 開発環境の準備

### [クイック スタート:Visual Studio を使用して Azure で初めての関数を作成する](https://docs.microsoft.com/ja-jp/azure/azure-functions/functions-create-your-first-function-visual-studio)

ローカルデバッグ用の local.settings.json ファイル

```json:local.settings.json
{
  "IsEncrypted": false,
  "Values": {
    "AzureWebJobsStorage": "<Azure Functions 既定の Azure Storage 接続文字列>",
    "AudioStorage": "<音声ファイル格納用 Azure Storage 接続文字列>",
    "CognitiveEndpoint": "wss://<カスタムドメイン名>.cognitiveservices.azure.com/stt/speech/recognition/conversation/cognitiveservices/v1",
    CognitiveEndpointId": "<カスタムスピーチのエンドポイント ID>",
    "CognitiveServiceApiKey": "<Cognitive.SpeechService API キー>",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet"
  },
  "ConnectionStrings": {}
}
```

※ 上記例で CognitiveEndpoint の wss で始まる個別名は Speech Service のネットワークでカスタムドメイン名を付けて VNET からプライベートエンドポイント経由でアクセスする際の例となっています。また CognitiveEndpointId はカスタムスピーチで独自の学習モデルを公開した際のエンドポイントID を指定します。

## Functions.cs

```csharp:Functions.cs
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
            using (FileStream fs = File.OpenWrite(tempfile + ".txt"))
            {
                // Speech サービスへ接続
                string key = System.Environment.GetEnvironmentVariable("CognitiveServiceApiKey");
                string endPoint = System.Environment.GetEnvironmentVariable("CognitiveEndpoint");
                string endPointId = System.Environment.GetEnvironmentVariable("CognitiveEndpointId");
                Uri uriEndpoint;
                try
                {
                    uriEndpoint = new Uri(endPoint);
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex.Message);
                    return;
                }

                // カスタムドメインを利用した場合のエンドポイント指定
                SpeechConfig speechConfig = SpeechConfig.FromEndpoint(uriEndpoint, key);
                // endPointId は、カスタムスピーチ利用の場合のみ指定
                speechConfig.EndpointId = endPointId;
                speechConfig.SpeechRecognitionLanguage = "ja-JP";
                AudioConfig audioConfig = AudioConfig.FromWavFileInput(tempfile + ".wav");
                SpeechRecognizer recognizer = new SpeechRecognizer(speechConfig, audioConfig);

                // イベントタスクの同期用
                var stopRecognition = new TaskCompletionSource<int>();

                // 部分文字列の抽出毎に繰り返し呼ばれる
                recognizer.Recognized += (s, e) =>
                {
                    if (e.Result.Reason == ResultReason.RecognizedSpeech)
                    {
                        String transcript = e.Result.Text;
                        log.LogInformation("RECOGNIZED: Text=" + transcript);
                        try
                        {
                            Byte[] info = new UTF8Encoding(true).GetBytes(transcript);
                            fs.Write(info, 0, info.Length);
                        }
                        catch (Exception ex)
                        {
                            log.LogWarning(ex.Message);
                        }
                    }
                    else if (e.Result.Reason == ResultReason.NoMatch)
                    {
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
            }
        }
    }
}
```

## 参考資料

### [音声ファイルの終わりまで継続的認識](https://docs.microsoft.com/ja-jp/azure/cognitive-services/speech-service/get-started-speech-to-text?tabs=windowsinstall&pivots=programming-language-csharp#continuous-recognition)
