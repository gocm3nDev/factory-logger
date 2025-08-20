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
    private const int Port = 5000;
    private const string ipAddress = "127.0.0.1";
    private static CancellationTokenSource _loggingCts;
    private static int _activeClients = 0;

    private static bool _isServer = false;
    public static bool shouldRestartAsClient = false;

    static async Task Main(string[] args)
    {
        await RunAsResilientNodeAsync();
    }

    static async Task RunAsResilientNodeAsync()
    {
        while (true)
        {
            try
            {
                Log.Logger = ConfigureLogger("CLIENT");
                Log.Warning("Client rolü alındı");
                await RunAsClientAsync();

                if (_isServer && !shouldRestartAsClient)
                {
                    continue;
                }
            }
            catch
            {
                Log.Logger = ConfigureLogger("SERVER");
                Log.Warning("Server rolü alındı");
                _isServer = true;
                await RunAsServerAsync();
            }
        }
    }

    static async Task RunAsClientAsync()
    {
        _isServer = await IsServerOnline();

        using var client = new TcpClient();
        _isServer = false;
        using var cts = new CancellationTokenSource();

        try
        {
            await client.ConnectAsync(ipAddress, Port);
            Log.Information("Server ile bağlantı kuruldu");

            var monitorTask = MonitorServerConnection(client, cts.Token);
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
            Log.Error("Server bulunamadı. Server rolüne geçiliyor");
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
                    Log.Information("Server bağlantısı kayboldu, rol değişimi başlatılıyor");
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

    static async Task SendRoleSwitchRequest(CancellationToken token)
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
                            Log.Information("Rol değiştirme isteği gönderildi");
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
            Log.Information("Server olarak başlatıldı");

            StartServerLoggingIfNoClients();

            while (true)
            {
                var client = await listener.AcceptTcpClientAsync();

                if (shouldRestartAsClient)
                {
                    Log.Information("Rol değişimi için server kapanıyor");
                    listener.Stop();
                    await Task.Delay(1000);

                    shouldRestartAsClient = false;
                    _isServer = false;
                    return;
                }

                Interlocked.Increment(ref _activeClients);
                StopServerLoggingIfClientsExist();

                _ = HandleClientConnectionAsync(client);
            }
        }
        catch (SocketException ex)
        {
            Log.Error($"Socket hatası oluştu: {ex.Message}");
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
                        Log.Information("Rol değiştirme isteği alındı.");
                        shouldRestartAsClient = true;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"Client bağlantısı kesildi veya hata oluştu: {ex.Message}");
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