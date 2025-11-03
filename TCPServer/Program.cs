﻿using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Text.Json;
using System.IO;

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
}

static class Logger
{
    private static readonly string logDir = "/home/thaiyal/Desktop/TCPIP/TCPIP/TCP_IP/TCPServer/Logs";
    private static readonly string logFile;

    static Logger()
    {
        if (!Directory.Exists(logDir))
            Directory.CreateDirectory(logDir);

        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        logFile = Path.Combine(logDir, $"ODSLog_{timestamp}.txt");
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}";
        Console.WriteLine(line);
        try
        {
            File.AppendAllText(logFile, line + Environment.NewLine);
        }
        catch
        {
            // fail silently if writing to file fails
        }
    }
}

class TestSummary
{
    public string SiteNo { get; set; }
    public string Barcode { get; set; }
    public string Bin { get; set; }
}

class ODSServer
{
    static TcpListener listener;
    static bool isRunning = true;
    static List<TcpClient> clientList = new();
    static List<DUT> dutList = new();
    static int nextDUTIndex = 0;
    static Dictionary<string, DUT> ipDUTMap = new();
    static readonly object dutLock = new();
    static HashSet<string> allowedIPs = new();
    static Dictionary<string, double> siteTemperatures = new();
    static List<TestSummary> overallSummary = new();

    static int configPort = 5000;

    static void Main()
    {
        GenerateDUTList(25);
        LoadConfig();

        Logger.Info($"[ODS Server] Listening on port {configPort}...");
        listener = new TcpListener(IPAddress.Any, configPort);
        listener.Start();

        Thread acceptThread = new Thread(AcceptClients);
        acceptThread.Start();

        Logger.Info("Type 'exit' to stop server.");
        while (Console.ReadLine()?.ToLower() != "exit") { }

        isRunning = false;
        listener.Stop();

        lock (clientList)
        {
            foreach (var c in clientList) c.Close();
        }

        PrintOverallSummary();
        Logger.Info("[ODS Server] Stopped.");
    }

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

    static void LoadConfig()
    {
        string jsonPath = "/home/thaiyal/Desktop/TCPIP/TCPIP/TCP_IP/TCPServer/handlerpc.json";
        if (!File.Exists(jsonPath))
        {
            Logger.Error($"Config file not found: {jsonPath}");
            return;
        }

        var jsonText = File.ReadAllText(jsonPath);
        var config = JsonSerializer.Deserialize<HandlerConfig>(jsonText);
        allowedIPs = new HashSet<string>(config.AcceptanceIp ?? new List<string>());
        configPort = config.Port;

        Logger.Info($"Allowed IPs loaded: {string.Join(", ", allowedIPs)}");
        Logger.Info($"Port loaded from config: {configPort}");
    }

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
            catch
            {
                if (!isRunning) break;
            }
        }
    }

    static void HandleClient(TcpClient client)
    {
        string clientIP = ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString();
        if (!allowedIPs.Contains(clientIP))
        {
            Logger.Warn($"[{clientIP}] Connection rejected. Not in allowed IP list.");
            client.Close();
            return;
        }

        lock (clientList)
        {
            if (clientList.Exists(c => ((IPEndPoint)c.Client.RemoteEndPoint).Address.ToString() == clientIP))
            {
                Logger.Warn($"[{clientIP}] Duplicate connection detected. Closing new one.");
                client.Close();
                return;
            }
            clientList.Add(client);
        }

        Logger.Info($"[{clientIP}] Connected.");

        try
        {
            NetworkStream stream = client.GetStream();
            byte[] buffer = new byte[1024];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                string msg = Encoding.UTF8.GetString(buffer, 0, read).Trim();
                Logger.Info($"[{clientIP}] Received: {msg}");
                string response = ProcessCommand(msg, clientIP);
                if (!string.IsNullOrEmpty(response))
                {
                    byte[] responseBytes = Encoding.UTF8.GetBytes(response + "\n");
                    stream.Write(responseBytes, 0, responseBytes.Length);
                    Logger.Info($"[{clientIP}] Sent: {response}");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[{clientIP}] Error: {ex.Message}");
        }
        finally
        {
            lock (clientList) clientList.Remove(client);
            lock (dutLock) ipDUTMap.Remove(clientIP);
            client.Close();
            Logger.Info($"[{clientIP}] Disconnected.");
        }
    }

    static string GetSiteNoFromIP(string ip)
    {
        string[] parts = ip.Split('.');
        if (parts.Length > 0 && int.TryParse(parts[^1], out int siteNo))
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
            if (end > start)
                siteNo = trimmedCmd.Substring(start, end - start).Trim();

            if (string.IsNullOrEmpty(siteNo))
                return ">>INVALID_FORMAT_NO_SITENO";

            lock (dutLock)
            {
                if (!ipDUTMap.ContainsKey(clientIP))
                {
                    if (nextDUTIndex >= dutList.Count)
                    {
                        PrintOverallSummary();
                        return $"<<*%UID=LOTEND%*>>";
                    }

                    var dut = dutList[nextDUTIndex++];
                    ipDUTMap[clientIP] = dut;
                }

                var assignedDUT = ipDUTMap[clientIP];
                return $"<<{siteNo}%GETDUTINFO%UID={assignedDUT.UID};BCD={assignedDUT.BCD};WARPAGE={assignedDUT.Warpage};TESTCOUNT={assignedDUT.TestCount}>>";
            }
        }

        // SetTestResult
        if (trimmedCmd.StartsWith("<<*%SetTestResult%"))
        {
            string content = trimmedCmd.Substring("<<*%SetTestResult%".Length);
            if (content.EndsWith("%*>>")) content = content[..^4];
            else if (content.EndsWith(">>")) content = content[..^2];

            string bin = "", bcd = "";
            foreach (var part in content.Split(';'))
            {
                if (part.StartsWith("BIN=")) bin = part[4..].Trim();
                if (part.StartsWith("BCD=")) bcd = part[4..].Trim();
            }

            if (string.IsNullOrEmpty(bin) || string.IsNullOrEmpty(bcd))
                return "<<*%SETTESTRESULT%NAK;MISSING_PARAMETERS>>";

            lock (dutLock)
            {
                if (ipDUTMap.TryGetValue(clientIP, out var assignedDUT) && bcd == assignedDUT.BCD)
                {
                    Logger.Info($"[{clientIP}] [SetTestResult] BCD matched. BIN={bin}, BCD={bcd}");
                    overallSummary.Add(new TestSummary
                    {
                        SiteNo = GetSiteNoFromIP(clientIP),
                        Barcode = bcd,
                        Bin = bin
                    });
                    ipDUTMap.Remove(clientIP);
                    return "<<*%SETTESTRESULT%ACK>>";
                }
                else
                {
                    Logger.Warn($"[{clientIP}] [SetTestResult] BCD mismatch or no DUT assigned.");
                    return "<<*%SETTESTRESULT%NAK;INVALID_BCD>>";
                }
            }
        }

        // SetSiteTemp
        if (trimmedCmd.StartsWith("<<") && trimmedCmd.Contains("%SetSiteTemp%"))
        {
            try
            {
                int start = trimmedCmd.IndexOf("<<") + 2;
                int end = trimmedCmd.IndexOf('%', start);
                string siteNo = trimmedCmd.Substring(start, end - start).Trim();
                if (string.IsNullOrEmpty(siteNo) || siteNo == "*")
                    siteNo = GetSiteNoFromIP(clientIP);

                string tempStr = trimmedCmd.Substring(trimmedCmd.IndexOf("%SetSiteTemp%") + "%SetSiteTemp%".Length)
                                       .Replace(">>", "")
                                       .Trim();

                if (!double.TryParse(tempStr, out double tempValue))
                    return $"<<{siteNo}%SETSITETEMP%TIMEOUT;INVALID_VALUE>>";

                if (tempValue < -40 || tempValue > 150)
                    return $"<<{siteNo}%SETSITETEMP%TIMEOUT;OUT_OF_RANGE>>";

                lock (siteTemperatures)
                    siteTemperatures[siteNo] = tempValue;

                Logger.Info($"[Site {siteNo}] Temperature set to {tempValue}°C");
                return $"<<{siteNo}%SETSITETEMP%ACK>>";
            }
            catch (Exception ex)
            {
                return $"<<*%SETSITETEMP%TIMEOUT;{ex.Message}>>";
            }
        }

        // GetSiteNo
        if (trimmedCmd == "<<*%GetSiteNo%*>>")
        {
            string siteNo = GetSiteNoFromIP(clientIP);
            return $"<<*%GETSITENO%SITENO={siteNo}>>";
        }

        // GetStatus
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

        // Default commands
        return trimmedCmd switch
        {
            "<<*%GetID%*>>" => "<<*%GETID%MODEL=3200;SN=1234567;NAME=SLT001;SWVERSION=1.1.0.1>>",
            "<<*%GetLotInfo%*>>" => "<<*%GETLOTINFO%LOTID=TY08;OPERATORID=0001>>",
            "<<*%GetHandlerStatus%*>>" => "<<*%GETHANDLERSTATUS%STATUS=CYCLE>>",
            "<<*%EnableTempLog%*>>" => "<<*%ENABLETEMPLOG%ACK>>",
            "<<*%DisableTempLog%*>>" => "<<*%DISABLETEMPLOG%ACK>>",
            "<<*%EnableTSD%*>>" => "<<*%ENABLETSD%ACK>>",
            "<<*%DisableTSD%*>>" => "<<*%DISABLETSD%ACK>>",
            _ => ">>UNKNOWN_COMMAND"
        };
    }

    static void PrintOverallSummary()
    {
        if (overallSummary.Count == 0)
        {
            Logger.Info("No test results to summarize.");
            return;
        }

        Logger.Info("==== OVERALL TEST SUMMARY ====");
        Logger.Info($"{"Site",-8} {"Barcode",-15} {"Bin",-5}");
        Logger.Info(new string('-', 35));

        foreach (var s in overallSummary)
        {
            Logger.Info($"{s.SiteNo,-8} {s.Barcode,-15} {s.Bin,-5}");
        }

        string summaryPath = "/home/thaiyal/Desktop/TCPIP/TCPIP/TCP_IP/TCPServer/Logs/OverallSummary.txt";
        var sb = new StringBuilder();
        sb.AppendLine("SiteNo,Barcode,Bin");
        foreach (var s in overallSummary)
            sb.AppendLine($"{s.SiteNo},{s.Barcode},{s.Bin}");
        File.WriteAllText(summaryPath, sb.ToString());

        Logger.Info($"Overall summary saved to {summaryPath}");
    }
}