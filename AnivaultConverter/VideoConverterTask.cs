using Coravel.Invocable;
using FFMpegCore;
using FFMpegCore.Arguments;
using FFMpegCore.Enums;

namespace AnivaultConverter;

public class VideoConverterTask : IInvocable, ICancellableInvocable
{
    private const string DownloadingPrefix = "downloading_";
    private const string H264Codec = "h264";
    private readonly string[] _availableExtensions = [".mp4", ".mkv"];
    private readonly string _downloadingFolderPath;
    private readonly ILogger<VideoConverterTask> _log;
    private readonly string _toWatchFolderPath;

    public VideoConverterTask(IConfiguration configuration, ILogger<VideoConverterTask> log)
    {
        _downloadingFolderPath = configuration["DownloadingFolderPath"] ??
                                 throw new InvalidOperationException("DownloadingFolderPath is missing");
        _toWatchFolderPath = configuration["ToWatchFolderPath"] ??
                             throw new InvalidOperationException("ToWatchFolderPath is missing");
        _log = log;
    }

    public CancellationToken CancellationToken { get; set; }

    public async Task Invoke()
    {
        var directoryInfo = new DirectoryInfo(_downloadingFolderPath);
        var fileToConvertList = directoryInfo.GetFiles()
            .Where(fi => _availableExtensions.Contains(fi.Extension) && !fi.Name.StartsWith(DownloadingPrefix))
            .ToList();
        List<Task> runningConversion = [];
        foreach (FileInfo fileToConvert in fileToConvertList)
        {
            if (runningConversion.Count(t => !t.IsCompleted) >= 2)
            {
                await Task.WhenAny(runningConversion);
                CancellationToken.ThrowIfCancellationRequested();
            }

            try
            {
                IMediaAnalysis mediaInfo =
                    await FFProbe.AnalyseAsync(fileToConvert.FullName, cancellationToken: CancellationToken);
                SubtitleStream? subs = mediaInfo.SubtitleStreams.FirstOrDefault(s => s.Language == "ita");
                if (mediaInfo.VideoStreams.First().CodecName != H264Codec && subs == null)
                {
                    File.Move(fileToConvert.FullName, Path.Combine(_toWatchFolderPath, fileToConvert.Name));
                    continue;
                }

                if (subs is null)
                {
                    runningConversion.Add(ConvertVideo(fileToConvert));
                    DeleteFile(fileToConvert);
                    return;
                }

                var subsIndex = mediaInfo.SubtitleStreams.IndexOf(subs);
                runningConversion.Add(ConvertAndPrintSubs(fileToConvert, subsIndex));
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Error processing file {file}", fileToConvert.Name);
            }
        }

        //if i have just 2 files, the foreach ends without waiting for the 2 task, with this i'm sure i wait for everything to complete  
        await Task.WhenAll(runningConversion);
    }

    private async Task ConvertAndPrintSubs(FileInfo fileToConvert, int subIndex)
    {
        try
        {
            var subtitleOptions = SubtitleHardBurnOptions.Create(fileToConvert.FullName);
            subtitleOptions.SetSubtitleIndex(subIndex);
            _log.Info("Inizio la conversione e stampaggio dei sottotitoli del file {filename}", fileToConvert.Name);

            await FFMpegArguments
                // -i "filepath"
                .FromFileInput(fileToConvert.FullName, true, options => options
                    .WithHardwareAcceleration(HardwareAccelerationDevice.QSV)
                    // .WithCustomArgument("-hwaccel qsv")     // Inizializza l'hardware QSV
                    .WithCustomArgument("-hwaccel_output_format nv12")
                    .WithCustomArgument("-c:v h264_qsv")
                )
                .OutputToFile(Path.Combine(_toWatchFolderPath, fileToConvert.Name), true, options => options
                    .WithCustomArgument("-map 0:v:0")
                    .WithCustomArgument("-map 0:a:0")
                    .WithVideoCodec("hevc_qsv")
                    .WithCustomArgument("-global_quality 24")
                    .WithSpeedPreset(Speed.Medium)
                    .WithVideoFilters(filters => filters
                        .HardBurnSubtitle(subtitleOptions)
                    )

                    // -c:a copy
                    .WithAudioCodec(AudioCodec.Copy)
                )
                .NotifyOnError(Console.WriteLine)
                .CancellableThrough(CancellationToken)
                .ProcessAsynchronously();

            _log.Info("Conversione e stampaggio dei sottotitoli del file {filename} completata", fileToConvert.Name);
            DeleteFile(fileToConvert);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Error processing file {file}", fileToConvert.Name);
        }
    }

    private async Task ConvertVideo(FileInfo fileToConvert)
    {
        try
        {
            _log.Info("Inizio la conversione del video");
            await FFMpegArguments
                .FromFileInput(fileToConvert.FullName, true, options => options
                    .WithHardwareAcceleration(HardwareAccelerationDevice.QSV)
                    // .WithCustomArgument("-hwaccel qsv")     // Inizializza l'hardware QSV
                    .WithCustomArgument("-hwaccel_output_format nv12")
                    .WithCustomArgument("-c:v h264_qsv")
                )
                .OutputToFile(Path.Combine(_toWatchFolderPath, fileToConvert.Name), true, options => options
                    .WithCustomArgument("-map 0:v:0")
                    .WithCustomArgument("-map 0:a:0")
                    .WithVideoCodec("hevc_qsv")
                    .WithSpeedPreset(Speed.Medium)
                    .WithCustomArgument("-global_quality 24")
                    .WithAudioCodec(AudioCodec.Copy)
                )
                .NotifyOnError(Console.WriteLine)
                .CancellableThrough(CancellationToken)
                .ProcessAsynchronously();

            _log.Info("Conversione del video del file {filename} completata", fileToConvert.Name);
            DeleteFile(fileToConvert);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Error processing file {file}", fileToConvert.Name);
        }
    }

    private void DeleteFile(FileInfo fileToDelete)
    {
        File.Delete(fileToDelete.FullName);
    }
}