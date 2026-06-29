using System.Globalization;
using System.Runtime.InteropServices;
using ManagedBass;
using ManagedBass.Asio;

internal static class Program
{
    private const string NativeDllHint = "Verify scripts/BassAsioMonitor/runtimes/win-x64/native/bass.dll and bassasio.dll exist and are copied beside the app output.";

    public static int Main(string[] args)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                throw new AppException("BassAsioMonitor is Windows x64 only.");

            if (!Environment.Is64BitProcess)
                throw new AppException("BassAsioMonitor requires a 64-bit process. Build/run with win-x64.");

            if (!Options.TryParse(args, out var options, out var error))
            {
                Console.Error.WriteLine(error);
                Console.Error.WriteLine();
                PrintUsage();
                return 2;
            }

            if (options.Help)
            {
                PrintUsage();
                return 0;
            }

            Console.WriteLine("BASS/BASSASIO low-latency ASIO input monitor");
            Console.WriteLine("WARNING: use headphones or low monitor volume. Open mic + speakers can cause feedback.");
            Console.WriteLine();

            ReportNativeVersions();
            BassAsio.Unicode = true;

            var deviceCount = BassAsio.DeviceCount;
            if (deviceCount <= 0)
                throw new AppException("No ASIO devices found. Install USB interface ASIO driver/control panel, then retry.");

            if (options.ListDevices)
            {
                PrintDevices(deviceCount);
                return 0;
            }

            if (options.Device < 0 || options.Device >= deviceCount)
                throw new AppException($"Invalid device {options.Device}. Available device indexes: 0..{deviceCount - 1}.");

            return RunMonitor(options);
        }
        catch (AppException ex)
        {
            Console.Error.WriteLine("ERROR: " + ex.Message);
            return 1;
        }
        catch (Exception ex) when (IsNativeLoadException(ex))
        {
            Console.Error.WriteLine("ERROR: Could not load BASS/BASSASIO native DLLs. " + NativeDllHint);
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR: " + ex.Message);
            return 1;
        }
    }

    private static int RunMonitor(Options options)
    {
        var asioInitialized = false;
        var quit = new ManualResetEventSlim(false);

        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            quit.Set();
        };

        Console.CancelKeyPress += cancelHandler;

        try
        {
            if (!BassAsio.Init(options.Device, AsioInitFlags.Thread))
                throw new AppException($"Failed to initialize ASIO device {options.Device}: {BassAsio.LastError}. Close DAWs/audio apps using ASIO, then retry.");

            asioInitialized = true;

            var info = BassAsio.Info;
            ValidateChannels(options, info);

            PrintSelectedDevice(options.Device, info);
            Console.WriteLine($"Input latency:  {BassAsio.GetLatency(true)} samples");
            Console.WriteLine($"Output latency: {BassAsio.GetLatency(false)} samples");
            Console.WriteLine();

            Console.WriteLine("Routing:");
            Console.WriteLine("  " + DescribeChannel(true, options.Input));
            Console.WriteLine("  " + DescribeChannel(false, options.OutputLeft));
            Console.WriteLine("  " + DescribeChannel(false, options.OutputRight));
            Console.WriteLine();

            ConfigureFormats(options.Input, options.OutputLeft, options.OutputRight);
            EnableMirror(options.OutputLeft, options.Input, "left");
            EnableMirror(options.OutputRight, options.Input, "right");
            SetVolume(options.OutputLeft, options.Gain, "left");
            SetVolume(options.OutputRight, options.Gain, "right");

            if (!BassAsio.Start(options.Buffer, 1))
                throw new AppException($"Failed to start ASIO processing with buffer {options.Buffer}: {BassAsio.LastError}.");

            Console.WriteLine($"Monitoring started: input {options.Input} -> outputs {options.OutputLeft}/{options.OutputRight}, gain {options.Gain.ToString(CultureInfo.InvariantCulture)}, buffer {options.Buffer}.");
            Console.WriteLine("Press Enter or Ctrl+C to stop.");

            var enterTask = Task.Run(() => Console.ReadLine());
            while (!quit.Wait(100))
            {
                if (enterTask.IsCompleted)
                    break;
            }

            Console.WriteLine();
            Console.WriteLine("Stopping...");
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;

            if (asioInitialized)
            {
                try
                {
                    if (BassAsio.IsStarted)
                        BassAsio.Stop();
                }
                catch
                {
                    // Best-effort shutdown.
                }

                try
                {
                    BassAsio.Free();
                }
                catch
                {
                    // Best-effort shutdown.
                }
            }

            quit.Dispose();
        }
    }

    private static void ReportNativeVersions()
    {
        try
        {
            Console.WriteLine("BASS version:     " + Bass.Version);
            Console.WriteLine("BASSASIO version: " + BassAsio.Version);
            Console.WriteLine();
        }
        catch (Exception ex) when (IsNativeLoadException(ex))
        {
            throw new AppException("Could not load BASS/BASSASIO native DLLs. " + NativeDllHint + " " + ex.Message);
        }
    }

    private static void PrintDevices(int deviceCount)
    {
        Console.WriteLine("ASIO devices:");
        for (var i = 0; i < deviceCount; i++)
        {
            var device = BassAsio.GetDeviceInfo(i);
            Console.WriteLine($"  {i}: {device.Name} ({device.Driver})");
        }
    }

    private static void PrintSelectedDevice(int deviceIndex, AsioInfo info)
    {
        Console.WriteLine($"Device {deviceIndex}: {info.Name}");
        Console.WriteLine($"Driver version: {info.DriverVersion}");
        Console.WriteLine($"Sample rate:    {BassAsio.Rate} Hz");
        Console.WriteLine($"Inputs:         {info.Inputs}");
        Console.WriteLine($"Outputs:        {info.Outputs}");
        Console.WriteLine($"Buffer min:     {info.MinBufferLength} samples");
        Console.WriteLine($"Buffer max:     {info.MaxBufferLength} samples");
        Console.WriteLine($"Buffer prefer:  {info.PreferredBufferLength} samples");
        Console.WriteLine($"Buffer gran:    {info.BufferLengthGranularity}");
        Console.WriteLine($"Init flags:     {info.InitFlags}");
    }

    private static void ValidateChannels(Options options, AsioInfo info)
    {
        if (options.Input < 0 || options.Input >= info.Inputs)
            throw new AppException($"Invalid input channel {options.Input}. Available inputs: 0..{info.Inputs - 1}.");

        if (options.OutputLeft < 0 || options.OutputLeft >= info.Outputs)
            throw new AppException($"Invalid left output channel {options.OutputLeft}. Available outputs: 0..{info.Outputs - 1}.");

        if (options.OutputRight < 0 || options.OutputRight >= info.Outputs)
            throw new AppException($"Invalid right output channel {options.OutputRight}. Available outputs: 0..{info.Outputs - 1}.");

        if (options.OutputLeft == options.OutputRight)
            throw new AppException("Left and right output channels must differ. ASIO output channels are mono.");
    }

    private static string DescribeChannel(bool input, int channel)
    {
        var info = BassAsio.ChannelGetInfo(input, channel);
        var currentFormat = BassAsio.ChannelGetFormat(input, channel);
        var direction = input ? "input" : "output";
        return $"{direction} {channel}: {info.Name}, group {info.Group}, native {info.Format}, current {currentFormat}";
    }

    private static void ConfigureFormats(int input, int outputLeft, int outputRight)
    {
        var inputNative = BassAsio.ChannelGetInfo(true, input).Format;
        var leftNative = BassAsio.ChannelGetInfo(false, outputLeft).Format;
        var rightNative = BassAsio.ChannelGetInfo(false, outputRight).Format;

        var inputFloat = TrySetFormat(true, input, AsioSampleFormat.Float, out var inputFloatError);
        var leftFloat = TrySetFormat(false, outputLeft, AsioSampleFormat.Float, out var leftFloatError);
        var rightFloat = TrySetFormat(false, outputRight, AsioSampleFormat.Float, out var rightFloatError);

        if (inputFloat && leftFloat && rightFloat)
        {
            Console.WriteLine("Channel format: Float");
            return;
        }

        Console.WriteLine("Float channel format unavailable; trying native matching format.");
        Console.WriteLine($"  input float:  {(inputFloat ? "ok" : inputFloatError)}");
        Console.WriteLine($"  left float:   {(leftFloat ? "ok" : leftFloatError)}");
        Console.WriteLine($"  right float:  {(rightFloat ? "ok" : rightFloatError)}");

        if (leftNative != inputNative || rightNative != inputNative)
        {
            throw new AppException(
                "ChannelEnableMirror requires matching formats. " +
                $"Input native={inputNative}, left native={leftNative}, right native={rightNative}. " +
                "Try different input/output channels or change driver sample format/control panel settings.");
        }

        var setInputNative = TrySetFormat(true, input, inputNative, out var inputNativeError);
        var setLeftNative = TrySetFormat(false, outputLeft, inputNative, out var leftNativeError);
        var setRightNative = TrySetFormat(false, outputRight, inputNative, out var rightNativeError);

        if (!setInputNative || !setLeftNative || !setRightNative)
        {
            throw new AppException(
                $"Failed to set matching native format {inputNative}. " +
                $"Input={inputNativeError}, left={leftNativeError}, right={rightNativeError}.");
        }

        Console.WriteLine($"Channel format: {inputNative}");
    }

    private static bool TrySetFormat(bool input, int channel, AsioSampleFormat format, out string error)
    {
        if (BassAsio.ChannelSetFormat(input, channel, format))
        {
            error = string.Empty;
            return true;
        }

        error = BassAsio.LastError.ToString();
        return false;
    }

    private static void EnableMirror(int output, int input, string label)
    {
        if (!BassAsio.ChannelEnableMirror(output, true, input))
        {
            var asioError = BassAsio.LastError;
            var inputInfo = BassAsio.ChannelGetInfo(true, input);
            var outputInfo = BassAsio.ChannelGetInfo(false, output);
            var inputFormat = BassAsio.ChannelGetFormat(true, input);
            var outputFormat = BassAsio.ChannelGetFormat(false, output);

            throw new AppException(
                $"Failed to enable {label} mirror input {input} -> output {output}: {asioError}. " +
                $"Input native/current={inputInfo.Format}/{inputFormat}, output native/current={outputInfo.Format}/{outputFormat}. " +
                "Try different channels or change driver sample format/control panel settings.");
        }
    }

    private static void SetVolume(int output, double gain, string label)
    {
        if (!BassAsio.ChannelSetVolume(false, output, gain))
            throw new AppException($"Failed to set {label} output {output} gain {gain.ToString(CultureInfo.InvariantCulture)}: {BassAsio.LastError}.");
    }

    private static bool IsNativeLoadException(Exception ex)
    {
        return ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException
            || ex.InnerException is not null && IsNativeLoadException(ex.InnerException);
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
Usage:
  BassAsioMonitor --list-devices
  BassAsioMonitor [--device <index>] [--input <index>] [--output-left <index>] [--output-right <index>] [--buffer <samples>] [--gain <value>]

Defaults:
  --device 0 --input 0 --output-left 0 --output-right 1 --buffer 0 --gain 1.0

Options:
  --list-devices          List ASIO devices and exit.
  --device <index>        ASIO device index.
  --input <index>         ASIO input channel index.
  --output-left <index>   ASIO left output channel index.
  --output-right <index>  ASIO right output channel index.
  --buffer <samples>      ASIO buffer length passed to Start. 0=current/default, -1=driver default if supported.
  --gain <value>          Output mirror gain. 1.0=unity, 0.5=-6 dB, 0=mute.
  --help                  Show this help.
""");
    }

    private sealed record Options
    {
        public bool ListDevices { get; private init; }
        public bool Help { get; private init; }
        public int Device { get; private init; }
        public int Input { get; private init; }
        public int OutputLeft { get; private init; }
        public int OutputRight { get; private init; } = 1;
        public int Buffer { get; private init; }
        public double Gain { get; private init; } = 1.0;

        public static bool TryParse(string[] args, out Options options, out string? error)
        {
            var result = new Options();
            error = null;

            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                switch (arg)
                {
                    case "--list-devices":
                        result = result with { ListDevices = true };
                        break;

                    case "--help":
                    case "-h":
                    case "/?":
                        result = result with { Help = true };
                        break;

                    case "--device":
                        if (!ReadInt(args, ref i, arg, out var device, out error))
                            return Fail(out options);
                        result = result with { Device = device };
                        break;

                    case "--input":
                        if (!ReadInt(args, ref i, arg, out var input, out error))
                            return Fail(out options);
                        result = result with { Input = input };
                        break;

                    case "--output-left":
                        if (!ReadInt(args, ref i, arg, out var outputLeft, out error))
                            return Fail(out options);
                        result = result with { OutputLeft = outputLeft };
                        break;

                    case "--output-right":
                        if (!ReadInt(args, ref i, arg, out var outputRight, out error))
                            return Fail(out options);
                        result = result with { OutputRight = outputRight };
                        break;

                    case "--buffer":
                        if (!ReadInt(args, ref i, arg, out var buffer, out error))
                            return Fail(out options);
                        if (buffer < -1)
                        {
                            error = "--buffer must be >= -1.";
                            return Fail(out options);
                        }
                        result = result with { Buffer = buffer };
                        break;

                    case "--gain":
                        if (!ReadDouble(args, ref i, arg, out var gain, out error))
                            return Fail(out options);
                        if (!double.IsFinite(gain) || gain < 0)
                        {
                            error = "--gain must be a finite value >= 0.";
                            return Fail(out options);
                        }
                        result = result with { Gain = gain };
                        break;

                    default:
                        error = $"Unknown option: {arg}";
                        return Fail(out options);
                }
            }

            options = result;
            return true;
        }

        private static bool ReadInt(string[] args, ref int index, string option, out int value, out string? error)
        {
            value = 0;
            if (++index >= args.Length)
            {
                error = $"{option} requires a value.";
                return false;
            }

            if (!int.TryParse(args[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                error = $"{option} requires an integer value.";
                return false;
            }

            error = null;
            return true;
        }

        private static bool ReadDouble(string[] args, ref int index, string option, out double value, out string? error)
        {
            value = 0;
            if (++index >= args.Length)
            {
                error = $"{option} requires a value.";
                return false;
            }

            if (!double.TryParse(args[index], NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                error = $"{option} requires a numeric value.";
                return false;
            }

            error = null;
            return true;
        }

        private static bool Fail(out Options options)
        {
            options = new Options();
            return false;
        }
    }

    private sealed class AppException(string message) : Exception(message);
}
