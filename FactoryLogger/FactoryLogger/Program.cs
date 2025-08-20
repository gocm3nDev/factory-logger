using Serilog;
using Serilog.Sinks.SystemConsole.Themes;
using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

class Program
{
    // global tanimlamalar
    private const int Port = 5000;
    private const string ipAddress = "127.0.0.1"; // localhost
    private static CancellationTokenSource _loggingCts;
    private static int _activeClients = 0;

    private static bool _isServer = false;
    public static bool shouldRestartAsClient = false;

    static async Task Main(string[] args)
    {
        await RunAsResilientNodeAsync();
    }

    static async Task RunAsResilientNodeAsync() // server/client olacagii belirleyen method
    {
        while (true)
        {
            try
            {
                Log.Logger = ConfigureLogger("CLIENT");
                Log.Warning("client rolu alindi");
                await RunAsClientAsync();
            }
            catch
            {
                Log.Logger = ConfigureLogger("SERVER");
                Log.Warning("server rolu alindi");
                _isServer = true;
                await RunAsServerAsync();
            }
        }
    }

    static async Task RunAsClientAsync() // client olarak calisacak method
    {
        _isServer = await IsServerOnline();

        using var client = new TcpClient();
        _isServer = false;
        using var cts = new CancellationTokenSource();

        try
        {
            await client.ConnectAsync(ipAddress, Port);
            Log.Information("server ile baglanti kuruldu");

            var monitorTask = MonitorServerConnection(client, cts.Token); // client varligini takip etmek icin
            var keyTask = SendRoleSwitchRequest(cts.Token);

            while (client.Connected && !_isServer && !shouldRestartAsClient)
            {
                Log.Information(DateTime.Now.ToString());
                await Task.Delay(1000);
            }

            cts.Cancel();
            await Task.WhenAll(monitorTask, keyTask);
        }
        catch (SocketException)
        {
            Log.Error("server bulunamadi. server rolune geciliyor");
            throw;
        }
    }

    static async Task MonitorServerConnection(TcpClient client, CancellationToken token)
    {
        try
        {
            while (client.Connected && !token.IsCancellationRequested)
            {
                if (!(await IsServerOnline()))
                {
                    Log.Information("server baglantisi kaybldu. rol degisimi baslatiliyor");
                    _isServer = true;
                    shouldRestartAsClient = false;
                    client.Close();
                    break;
                }
                await Task.Delay(1000, token);
            }
        }
        catch (OperationCanceledException) { }
    }

    static async Task SendRoleSwitchRequest(CancellationToken token) // rol degistirme istegini gonderen method
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (Console.KeyAvailable)
                {
                    var keyInfo = Console.ReadKey(true);
                    if (keyInfo.Key == ConsoleKey.R && keyInfo.Modifiers.HasFlag(ConsoleModifiers.Control))
                    {
                        using var client = new TcpClient();
                        await client.ConnectAsync(ipAddress, Port);
                        if (client.Connected)
                        {
                            Log.Information("rol degistirme istegi gonderildi");
                            byte[] message = Encoding.UTF8.GetBytes("ROLE_SWITCH");
                            await client.GetStream().WriteAsync(message, 0, message.Length);
                            _isServer = true;
                            break;
                        }
                    }
                }
                await Task.Delay(100, token);
            }
        }
        catch (OperationCanceledException) { }
    }

    static async Task RunAsServerAsync()
    {
        _isServer = true;
        var listener = new TcpListener(IPAddress.Any, Port);

        try
        {
            listener.Start();
            Log.Information("Server olarak baslatildi");

            StartServerLoggingIfNoClients();

            while (true)
            {
                var client = await listener.AcceptTcpClientAsync();

                if (shouldRestartAsClient)
                {
                    Log.Information("rol degisimi icin server kapaniyor");
                    listener.Stop();
                    await Task.Delay(1000);

                    Log.Logger = ConfigureLogger("CLIENT");
                    shouldRestartAsClient = false;
                    await RunAsClientAsync();
                    return;
                }

                Interlocked.Increment(ref _activeClients);
                StopServerLoggingIfClientsExist();

                _ = HandleClientConnectionAsync(client);
            }
        }
        catch (SocketException ex)
        {
            Log.Error($"socket hatasi olustu: {ex.Message}");
            throw;
        }
        finally
        {
            listener.Stop();
        }
    }

    static async Task HandleClientConnectionAsync(TcpClient client)
    {
        await Task.Run(async () =>
        {
            try
            {
                var stream = client.GetStream();
                byte[] buffer = new byte[1024];

                while (client.Connected)
                {
                    int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                    if (bytesRead == 0) break;

                    string message = Encoding.UTF8.GetString(buffer, 0, bytesRead).TrimEnd('\0');
                    if (message.Equals("ROLE_SWITCH", StringComparison.OrdinalIgnoreCase))
                    {
                        Log.Information("Rol degistirme istegi alindi.");
                        shouldRestartAsClient = true;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"client bağlantısı kesildi veya hata olustu: {ex.Message}");
            }
            finally
            {
                client.Close();
                Interlocked.Decrement(ref _activeClients);
                StartServerLoggingIfNoClients();
            }
        });
    }

    public static async Task<bool> IsServerOnline()
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(ipAddress, Port);
            return client.Connected;
        }
        catch { return false; }
    }

    private static async Task ServerLogTime(CancellationToken token)
    {
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                Log.Information(DateTime.Now.ToString());
                await Task.Delay(1000, token);
            }
        }
        catch (OperationCanceledException) { }
    }

    private static void StartServerLoggingIfNoClients()
    {
        if (_activeClients == 0 && _loggingCts == null)
        {
            _loggingCts = new CancellationTokenSource();
            _ = ServerLogTime(_loggingCts.Token);
        }
    }

    private static void StopServerLoggingIfClientsExist()
    {
        if (_activeClients > 0 && _loggingCts != null)
        {
            _loggingCts.Cancel();
            _loggingCts = null;
        }
    }

    private static ILogger ConfigureLogger(string role)
    {
        return new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console(
                theme: AnsiConsoleTheme.Literate,
                outputTemplate: "[{Timestamp:HH:mm:ss} " + role + " {Level:u3}] {Message:lj}{NewLine}{Exception}"
            )
            .CreateLogger();
    }
}
