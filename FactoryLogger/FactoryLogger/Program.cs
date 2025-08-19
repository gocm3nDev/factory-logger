using Microsoft.AspNetCore.Hosting.Server;
using Serilog;
using Serilog.Sinks.SystemConsole.Themes;
using System;
using System.Net;
using System.Net.NetworkInformation;
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
    private static CancellationTokenSource _listenerCts;
    private static bool _isServer = false;
    public static bool shouldRestartAsClient = false;

    static async Task Main(string[] args)
    {
        await RunAsResilientNodeAsync(); // server mi client mi olacagina karar veren metot
    }

    static async Task RunAsResilientNodeAsync()
    {
        while (true)
        {
            try // ilk olarak client olarak baslatmayi dene
            {
                Log.Warning("client rolu alindi");
                await RunAsClientAsync();
            }
            catch (Exception ex) // eger client yoksa serveri al
            {
                Log.Warning("server rolu alindi");
                _isServer = true;
                await RunAsServerAsync();
            }
        }
    }
    static async Task RunAsClientAsync()
    {
        _isServer = await isServerOnline(); // server online mi kontrol et
        Log.Logger = ConfigureLogger(_isServer ? "SERVER" : "CLIENT");

        TcpClient client = new TcpClient(); // client nesnesi olustur
        _isServer = false;

        try
        {
            await client.ConnectAsync(ipAddress, Port); // verilen ip ve port ile baglan
            Log.Information("sunucuyla baglanti kuruldu");

            _ = Task.Run(async () =>
            {
                while (client.Connected)
                {
                    if (!(await isServerOnline()))
                    {
                        Log.Information("Sunucu baglantisi kayboldu, rol degisimi baslatiliyor.");
                        _isServer = true;
                        shouldRestartAsClient = false; // restart değişkenini sıfırla
                        client.Close();
                        break;
                    }
                    await Task.Delay(1000);
                }
            });

            Task.Run(() => SendRoleSwitchRequest());

            while (client.Connected && !_isServer) // baglanti varsa ve server degilse
            {
                Log.Information(DateTime.Now.ToString()); // 1 saniyede bir zaman bilgisini logla
                await Task.Delay(1000);
            }
        }
        catch (SocketException)
        {
            Log.Error("Sunucuya baglanilamadi. Sunucu rolune geciliyor.");
            throw new Exception("Sunucuya baglanilamadi");
        }
        finally
        {
            if (client.Connected) // eger client bagliysa baglantiyi kapat
                client.Close();
        }
    }

    static async Task RunAsServerAsync()
    {
        _isServer = true;
        Log.Logger = ConfigureLogger("SERVER");

        TcpListener listener = new TcpListener(IPAddress.Any, Port);
        try
        {
            listener.Start(); // gelen istekleri dinlemeye basla
            Log.Information("Server olarak baslatildi");

            _loggingCts = new CancellationTokenSource();
            _ = ServerLogTime(_loggingCts.Token);

            while (true)
            {
                var client = await listener.AcceptTcpClientAsync(); // client baglantisi kabul et

                if (Program.shouldRestartAsClient)
                {
                    Log.Information("Rol degisimi icin server kapaniyor.");
                    listener.Stop();
                    // Loglamayı CLIENT rolüne geçiş öncesinde güncelleyelim
                    Log.Logger = ConfigureLogger("CLIENT");
                    await RunAsClientAsync();
                    Program.shouldRestartAsClient = false; // degiskeni sifirla
                    return; // ana dnguye don
                }

                // Zaten bir logging CTS varsa iptal et
                if (_loggingCts != null)
                {
                    _loggingCts.Cancel();
                    _loggingCts = null;
                }

                _ = HandleClientConnectionAsync(client); // client baglantisini asenkron olarak ele al
            }
        }
        catch (SocketException ex)
        {
            Log.Error($"Socket hatasi olustu: {ex.Message}");
            throw;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static ILogger ConfigureLogger(string role) // log ayarlarini server/client rolune gore konfigure et
    {
        return new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console(
                theme: AnsiConsoleTheme.Literate,
                outputTemplate: "[{Timestamp:HH:mm:ss} " + role + " {Level:u3}] {Message:lj}{NewLine}{Exception}"
            )
            .CreateLogger();
    }

    static async Task HandleClientConnectionAsync(TcpClient client) // client isteklerini ele al
    {
        await Task.Run(async () =>
        {
            try // baglanti olup olmadigini kontrol etmek icin stream nesnesinde veri oku
            {
                NetworkStream stream = client.GetStream(); // stream nesnesi olustur
                byte[] buffer = new byte[1024]; // 1 bytlik buffer yerine daha büyük bir buffer kullan

                while (client.Connected) // Baglantinin durumunu kontrol et
                {
                    int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);

                    if (bytesRead == 0) // Eger baglanti kapandiysa donguden cik
                    {
                        break;
                    }

                    string message = Encoding.UTF8.GetString(buffer, 0, bytesRead).TrimEnd('\0');

                    if (message.Equals("ROLE_SWITCH", StringComparison.OrdinalIgnoreCase)) // eger mesaj rol degistirme istegi ise
                    {
                        Log.Information("Rol degistirme istegi alindi.");
                        Program.shouldRestartAsClient = true;
                        _listenerCts?.Cancel();
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"Client baglantisi kesildi veya hata olustu: {ex.Message}");
            }
            finally
            {
                client.Close();
            }
        });
    }

    static async Task SendRoleSwitchRequest() // client => server rol degistirme istegi gonder
    {
        while (true)
        {
            ConsoleKeyInfo keyInfo = Console.ReadKey(true);

            if (keyInfo.Key == ConsoleKey.R && keyInfo.Modifiers.HasFlag(ConsoleModifiers.Control))
            {
                try
                {
                    using (TcpClient client = new TcpClient())
                    {
                        await client.ConnectAsync(ipAddress, Port).ConfigureAwait(false);
                        if (client.Connected)
                        {
                            Log.Information("Rol degistirme istegi gonderiliyor.");
                            byte[] message = Encoding.UTF8.GetBytes("ROLE_SWITCH");
                            await client.GetStream().WriteAsync(message, 0, message.Length).ConfigureAwait(false);
                            _isServer = true;
                            break;
                        }
                    }
                }
                catch (SocketException)
                {
                    Log.Error("Rol degistirme istegi gonderilemedi.");
                }
            }
        }

    }

    public static async Task<bool> isServerOnline() // serverin online olup olmadigini kontrol et
    {
        try
        {
            using (TcpClient client = new TcpClient()) // client nesnesi lusturup ip adresine ve portuna ping at
            {
                await client.ConnectAsync(ipAddress, Port).ConfigureAwait(false);
                if (client.Connected) // eger baglanti basariliysa true dondur
                    return true;
                else
                    return false;
            }
        }
        catch (SocketException)
        {
            Log.Warning("Server offline");
            return false;
        }
    }

    private static async Task ServerLogTime(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Log.Information(DateTime.Now.ToString());
                await Task.Delay(1000, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            Log.Information("Sunucu zaman loglama islemi iptal edildi.");
        }
    }
}