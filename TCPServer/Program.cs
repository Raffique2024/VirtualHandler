using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Text.Json;
using System.IO;
using System.Diagnostics;

// -----------------------------------------------------------------------
// FIXED PATH SYSTEM (All paths relative to program directory)
// -----------------------------------------------------------------------
static class ProgramPaths
{
    public static readonly string AppDir = AppContext.BaseDirectory;
    public static readonly string ConfigDir = Path.Combine(AppDir, "Configuration");

    public static readonly string ConfigPath = Path.Combine(ConfigDir, "handlerpc.json");
    public static readonly string CopyScript = Path.Combine(ConfigDir, "copy_to_docker.sh");
    public static readonly string LogsDir = Path.Combine(AppDir, "Logs");
}

// -----------------------------------------------------------------------
// MODELS
// -----------------------------------------------------------------------
class DUT
{
    public string UID { get; set; }
    public string BCD { get; set; }
    public string ECID { get; set; }
    public string Warpage { get; set; }
    public int TestCount { get; set; }
}

class HandlerConfig
{
    public string LotId { get; set; }
    public int Units { get; set; }
    public List<string> AcceptanceIp { get; set; }
    public int Port { get; set; } = 5000;
    public string WorkingDirectory { get; set; } = ProgramPaths.AppDir;
}

// -----------------------------------------------------------------------
// LOGGER
// -----------------------------------------------------------------------
static class Logger
{
    private static readonly string logFile;

    static Logger()
    {
        if (!Directory.Exists(ProgramPaths.LogsDir))
            Directory.CreateDirectory(ProgramPaths.LogsDir);

        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        logFile = Path.Combine(ProgramPaths.LogsDir, $"ODSLog_{timestamp}.txt");
    }

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg) => Write("ERROR", msg);

    private static void Write(string level, string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}";
        Console.WriteLine(line);

        try { File.AppendAllText(logFile, line + Environment.NewLine); }
        catch { }
    }
}

// -----------------------------------------------------------------------
// TEST SUMMARY MODEL
// -----------------------------------------------------------------------
class TestSummary
{
    public string SiteNo { get; set; }
    public string Barcode { get; set; }
    public string Bin { get; set; }
}

// -----------------------------------------------------------------------
// MAIN SERVER CLASS
// -----------------------------------------------------------------------
class ODSServer
{
    static TcpListener listener;
    static bool isRunning = true;

    static List<TcpClient> clientList = new();
    static List<DUT> dutList = new();
    static Dictionary<string, DUT> ipDUTMap = new();
    static Dictionary<string, double> siteTemperatures = new();

    static readonly object dutLock = new();
    static List<TestSummary> overallSummary = new();

    static int nextDUTIndex = 0;
    static int configPort = 5000;
    static HashSet<string> allowedIPs = new();
    static HandlerConfig config;

    // -------------------------------------------------------------------
    // MAIN
    // -------------------------------------------------------------------
    static void Main()
    {
        LoadConfig();
        GenerateDUTList(config.Units);

        StartListener();
        RunCopyScript();

        Console.WriteLine("Type 'exit' to stop server.");
        while (Console.ReadLine()?.ToLower() != "exit") { }

        StopServer();
    }

    // -------------------------------------------------------------------
    // GENERATE MOCK DUTS
    // -------------------------------------------------------------------
    static void GenerateDUTList(int count)
    {
        Random rand = new Random();

        for (int i = 1; i <= count; i++)
        {
            dutList.Add(new DUT
            {
                UID = i.ToString("00000"),
                BCD = $"MSFT{i:0000}",
                ECID = rand.Next(10000000, 99999999).ToString(),
                Warpage = (0.1 + rand.NextDouble() * 0.15).ToString("0.####"),
                TestCount = rand.Next(1, 6)
            });
        }

        Logger.Info($"Generated {count} DUT entries.");
    }

    // -------------------------------------------------------------------
    // CONFIG LOADING
    // -------------------------------------------------------------------
    static void LoadConfig()
    {
        if (!File.Exists(ProgramPaths.ConfigPath))
        {
            Logger.Error($"Config file not found: {ProgramPaths.ConfigPath}");
            Environment.Exit(1);
        }

        try
        {
            var json = File.ReadAllText(ProgramPaths.ConfigPath);
            config = JsonSerializer.Deserialize<HandlerConfig>(json);

            if (config == null)
            {
                Logger.Error("Failed to deserialize configuration file.");
                Environment.Exit(1);
            }

            allowedIPs = new HashSet<string>(config.AcceptanceIp ?? new List<string>());
            configPort = config.Port;

            Logger.Info($"Allowed IPs: {string.Join(", ", allowedIPs)}");
            Logger.Info($"Port loaded from config: {configPort}");
            Logger.Info($"WorkingDirectory: {config.WorkingDirectory}");
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to read config: {ex.Message}");
            Environment.Exit(1);
        }
    }

    // -------------------------------------------------------------------
    // RUN SHELL SCRIPT copy_to_docker.sh WITH SHA1 FIX
    // -------------------------------------------------------------------
    static void RunCopyScript()
    {
        if (!File.Exists(ProgramPaths.CopyScript))
        {
            Logger.Warn($"copy_to_docker.sh not found at: {ProgramPaths.CopyScript}");
            return;
        }

        try
        {
            Logger.Info("Preparing SHA1 file and SSH settings...");

            // SHA1 & ZIP files from WorkingDirectory in config
            string sha1Path = Path.Combine(config.WorkingDirectory, "SLT_TestProgram_release.sha1");
            string zipPath = Path.Combine(config.WorkingDirectory, "SLT_TestProgram_release.zip");

            if (File.Exists(sha1Path))
            {
                string sha1Content = File.ReadAllText(sha1Path).Trim();
                if (!sha1Content.Contains(" "))
                {
                    File.WriteAllText(sha1Path, $"{sha1Content}  {Path.GetFileName(zipPath)}");
                    Logger.Info($"SHA1 file updated with filename: {Path.GetFileName(zipPath)}");
                }
            }
            else
            {
                Logger.Warn($"SHA1 file not found: {sha1Path}");
            }

            // Remove offending SSH key automatically
            string sshKnownHosts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "known_hosts");
            string removeKeyCmd = $"ssh-keygen -f '{sshKnownHosts}' -R '[localhost]:2222'";

            Process removeKey = new Process();
            removeKey.StartInfo.FileName = "/bin/bash";
            removeKey.StartInfo.Arguments = $"-c \"{removeKeyCmd}\"";
            removeKey.StartInfo.UseShellExecute = false;
            removeKey.StartInfo.RedirectStandardOutput = true;
            removeKey.StartInfo.RedirectStandardError = true;
            removeKey.Start();
            string removeOut = removeKey.StandardOutput.ReadToEnd();
            string removeErr = removeKey.StandardError.ReadToEnd();
            removeKey.WaitForExit();

            if (!string.IsNullOrWhiteSpace(removeOut)) Logger.Info(removeOut);
            if (!string.IsNullOrWhiteSpace(removeErr)) Logger.Warn(removeErr);
            Logger.Info("Removed offending SSH key if it existed.");

            // Execute copy_to_docker.sh
            Logger.Info("Executing copy_to_docker.sh...");

            Process p = new Process();
            p.StartInfo.FileName = "/bin/bash";
            p.StartInfo.Arguments = $"\"{ProgramPaths.CopyScript}\"";
            p.StartInfo.UseShellExecute = false;
            p.StartInfo.RedirectStandardOutput = true;
            p.StartInfo.RedirectStandardError = true;

            p.StartInfo.Environment["SSH_OPTIONS"] = "-o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null";

            p.Start();

            string output = p.StandardOutput.ReadToEnd();
            string error = p.StandardError.ReadToEnd();

            p.WaitForExit();

            if (!string.IsNullOrWhiteSpace(output))
                Logger.Info(output);

            if (!string.IsNullOrWhiteSpace(error))
                Logger.Warn(error);

            Logger.Info("Script execution finished.");
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to run script: {ex.Message}");
        }
    }

    // -------------------------------------------------------------------
    // SERVER START
    // -------------------------------------------------------------------
    static void StartListener()
    {
        listener = new TcpListener(IPAddress.Any, configPort);
        listener.Start();

        Logger.Info($"Server listening on port {configPort}.");

        Thread acceptThread = new Thread(AcceptClients);
        acceptThread.Start();
    }

    static void StopServer()
    {
        isRunning = false;
        listener.Stop();

        lock (clientList)
            foreach (var c in clientList) c.Close();

        PrintOverallSummary();
        Logger.Info("Server stopped.");
    }

    // -------------------------------------------------------------------
    // ACCEPT CLIENTS
    // -------------------------------------------------------------------
    static void AcceptClients()
    {
        while (isRunning)
        {
            try
            {
                TcpClient client = listener.AcceptTcpClient();
                Thread t = new Thread(() => HandleClient(client));
                t.Start();
            }
            catch { if (!isRunning) break; }
        }
    }

    // -------------------------------------------------------------------
    // CLIENT HANDLER
    // -------------------------------------------------------------------
    static void HandleClient(TcpClient client)
    {
        string clientIP = ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString();

        if (!allowedIPs.Contains(clientIP))
        {
            Logger.Warn($"{clientIP} rejected.");
            client.Close();
            return;
        }

        lock (clientList) clientList.Add(client);
        Logger.Info($"{clientIP} connected.");

        try
        {
            NetworkStream stream = client.GetStream();
            byte[] buf = new byte[1024];
            int read;

            while ((read = stream.Read(buf, 0, buf.Length)) > 0)
            {
                string msg = Encoding.UTF8.GetString(buf, 0, read).Trim();
                Logger.Info($"{clientIP} RX: {msg}");

                string response = ProcessCommand(msg, clientIP);

                if (!string.IsNullOrEmpty(response))
                {
                    byte[] resp = Encoding.UTF8.GetBytes(response + "\n");
                    stream.Write(resp, 0, resp.Length);
                    Logger.Info($"{clientIP} TX: {response}");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"{clientIP} error: {ex.Message}");
        }
        finally
        {
            lock (clientList) clientList.Remove(client);
            lock (dutLock) ipDUTMap.Remove(clientIP);
            client.Close();

            Logger.Info($"{clientIP} disconnected.");
        }
    }

    // -------------------------------------------------------------------
    // COMMAND PROCESSING
    // -------------------------------------------------------------------
    static string GetSiteNoFromIP(string ip)
    {
        string[] p = ip.Split('.');
        if (p.Length > 0 && int.TryParse(p[^1], out int siteNo))
            return siteNo.ToString();
        return "0";
    }

    static string ProcessCommand(string command, string clientIP)
    {
        string trimmedCmd = command.TrimStart('$').Trim();

        // GetDUTInfo
        if (trimmedCmd.StartsWith("<<") && trimmedCmd.Contains("%GetDUTInfo%"))
        {
            string siteNo = "";
            int start = trimmedCmd.IndexOf("<<") + 2;
            int end = trimmedCmd.IndexOf('%', start);
            if (end > start) siteNo = trimmedCmd.Substring(start, end - start).Trim();
            if (string.IsNullOrEmpty(siteNo)) return ">>INVALID_FORMAT_NO_SITENO";

            lock (dutLock)
            {
                if (!ipDUTMap.ContainsKey(clientIP))
                {
                    if (nextDUTIndex >= dutList.Count)
                    {
                        PrintOverallSummary(); 
                        return $"<<*%UID=LOTEND%*>>";
                    }


                    ipDUTMap[clientIP] = dutList[nextDUTIndex++];
                }

                var d = ipDUTMap[clientIP];

                return $"<<{siteNo}%GETDUTINFO%UID={d.UID};BCD={d.BCD};WARPAGE={d.Warpage};TESTCOUNT={d.TestCount}>>";
            }
        }

         if (trimmedCmd.Contains("%GetStatus%"))
        {
            int start = trimmedCmd.IndexOf("<<") + 2;
            int end = trimmedCmd.IndexOf('%', start);
            string siteNo = (end > start) ? trimmedCmd.Substring(start, end - start).Trim() : "*";
            if (string.IsNullOrEmpty(siteNo) || siteNo == "*")
                siteNo = GetSiteNoFromIP(clientIP);

            double st;
            lock (siteTemperatures)
            {
                if (!siteTemperatures.TryGetValue(siteNo, out st))
                    st = 75.0; // default if not set
            }

            double tj = st - 10;
            double tc = st + 5;

            return $"<<{siteNo}%GETSITESTATUS%ST={st:0.0};TSD=ENABLE;TC={tc:0.0};TJ={tj:0.0};DUT=READYTOTEST;ERR=>>";
        }

        // SetTestResult
        if (trimmedCmd.StartsWith("<<*%SetTestResult%"))
        {
            string body = trimmedCmd.Replace("<<*%SetTestResult%", "").Replace("%*>>", "").Replace(">>", "");

            string bin = "", bcd = "";

            foreach (var part in body.Split(';'))
            {
                if (part.StartsWith("BIN=")) bin = part[4..].Trim();
                if (part.StartsWith("BCD=")) bcd = part[4..].Trim();
            }

            if (string.IsNullOrEmpty(bin) || string.IsNullOrEmpty(bcd))
                return "<<*%SETTESTRESULT%NAK;MISSING_PARAMETERS>>";

            lock (dutLock)
            {
                if (ipDUTMap.TryGetValue(clientIP, out var dut) && dut.BCD == bcd)
                {
                    overallSummary.Add(new TestSummary
                    {
                        SiteNo = GetSiteNoFromIP(clientIP),
                        Barcode = bcd,
                        Bin = bin
                    });

                    ipDUTMap.Remove(clientIP);
                    return "<<*%SETTESTRESULT%ACK>>";
                }

                return "<<*%SETTESTRESULT%NAK;INVALID_BCD>>";
            }
        }

        // SetSiteTemp
        if (trimmedCmd.Contains("%SetSiteTemp%"))
        {
            try
            {
                int start = trimmedCmd.IndexOf("<<") + 2;
                int end = trimmedCmd.IndexOf('%', start);
                string siteNo = trimmedCmd.Substring(start, end - start).Trim();
                if (string.IsNullOrEmpty(siteNo) || siteNo == "*")
                    siteNo = GetSiteNoFromIP(clientIP);

                string tempStr = trimmedCmd[(trimmedCmd.IndexOf("%SetSiteTemp%") + "%SetSiteTemp%".Length)..]
                                        .Replace(">>", "").Trim();

                if (!double.TryParse(tempStr, out double temp))
                    return $"<<{siteNo}%SETSITETEMP%TIMEOUT;INVALID_VALUE>>";

                if (temp < -40 || temp > 150)
                    return $"<<{siteNo}%SETSITETEMP%TIMEOUT;OUT_OF_RANGE>>";

                lock (siteTemperatures)
                    siteTemperatures[siteNo] = temp;

                return $"<<{siteNo}%SETSITETEMP%ACK>>";
            }
            catch
            {
                return $"<<*%SETSITETEMP%TIMEOUT;PARSE_ERROR>>";
            }
        }

        // Simple Commands
        return trimmedCmd switch
        {
            "<<*%GetID%*>>" => "<<*%GETID%MODEL=3200;SN=1234567;NAME=SLT001;SWVERSION=1.1.0.1>>",
           "<<*%GetLotInfo%*>>" => "<<*%GETLOTINFO%LOTID=TY08;OPERATORID=0001>>",
            "<<*%GetHandlerStatus%*>>" => "<<*%GETHANDLERSTATUS%STATUS=CYCLE>>",
            "<<*%EnableTempLog%*>>" => "<<*%ENABLETEMPLOG%ACK>>",
            "<<*%DisableTempLog%*>>" => "<<*%DISABLETEMPLOG%ACK>>",
            "<<*%EnableTSD%*>>" => "<<*%ENABLETSD%ACK>>",
            "<<*%DisableTSD%*>>" => "<<*%DISABLETSD%ACK>>",
            "<<*%GetSiteNo%*>>" => $"<<*%GETSITENO%SITENO={GetSiteNoFromIP(clientIP)}>>",
            _ => ">>UNKNOWN_COMMAND"
        };
    }

    // -------------------------------------------------------------------
    // SUMMARY
    // -------------------------------------------------------------------
    static void PrintOverallSummary()
    {
        if (overallSummary.Count == 0)
        {
            Logger.Info("No test results.");
            return;
        }

        string summaryPath = Path.Combine(ProgramPaths.LogsDir, "OverallSummary.txt");

        var sb = new StringBuilder();
        sb.AppendLine("SiteNo,Barcode,Bin");

        foreach (var s in overallSummary)
            sb.AppendLine($"{s.SiteNo},{s.Barcode},{s.Bin}");

        File.WriteAllText(summaryPath, sb.ToString());

        Logger.Info($"Summary saved: {summaryPath}");
    }
}
