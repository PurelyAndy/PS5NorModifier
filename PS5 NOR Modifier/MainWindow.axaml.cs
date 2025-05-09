using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using DialogHostAvalonia;
using Label = Avalonia.Controls.Label;

namespace PS5_NOR_Modifier;

public partial class MainWindow : Window
{
    private readonly Dictionary<string, string> Regions = new()
    {
        { "00", "Japan" },
        { "01", "US, Canada, (North America)" },
        { "15", "US, Canada, (North America)" },
        { "02", "Australia / New Zealand, (Oceania)" },
        { "03", "United Kingdom / Ireland" },
        { "04", "Europe / Middle East / Africa" },
        { "05", "South Korea" },
        { "06", "Southeast Asia / Hong Kong" },
        { "07", "Taiwan" },
        { "08", "Russia, Ukraine, India, Central Asia" },
        { "09", "Mainland China" },
        { "11", "Mexico, Central America, South America" },
        { "14", "Mexico, Central America, South America" },
        { "16", "Europe / Middle East / Africa" },
        { "18", "Singapore, Korea, Asia" }
    };
    
    private readonly SerialPort _uartSerial = new();
    
    public MainWindow()
    {
        InitializeComponent();
        RefreshComPorts();
    }

    private void RefreshComPorts()
    {
        string[] ports = SerialPort.GetPortNames();
        if (ports == null || ports.Length == 0)
        {
            ShowError("No available COM ports were detected.");
            return;
        }
        
        ComPorts.Items.Clear();
        foreach (string port in ports)
        {
            ComPorts.Items.Add(port);
        }
        ComPorts.SelectedIndex = 0;
        ConnectComButton.IsEnabled = true;
        DisconnectComButton.IsEnabled = false;
    }

    private void SponsorLink_OnClick(object? sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo("https://www.consolefix.shop")
        {
            UseShellExecute = true
        });
    }

    private void ClearOutputButton_OnClick(object? sender, RoutedEventArgs e)
    {
        OutputText.Text = "";
    }

    private void SendCustomCommandButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(CustomCommand.Text))
        {
            ShowError("Please enter a command to send via UART.");
            return;
        }

        if (!_uartSerial.IsOpen)
        {
            ShowError("Please connect to UART before attempting to send commands.");
            return;
        }
        
        try
        {
            List<string> UARTLines = [];

            string checksum = CalculateChecksum(CustomCommand.Text);
            _uartSerial.WriteLine(checksum);
            do
            {
                string? line = _uartSerial.ReadLine();
                if (!string.Equals($"{CustomCommand.Text}:{checksum}", line, StringComparison.InvariantCultureIgnoreCase))
                {
                    UARTLines.Add(line);
                }
            } while (_uartSerial.BytesToRead != 0);

            foreach (string l in UARTLines)
            {
                string[] split = l.Split(' ');
                if (!split.Any()) continue;
                
                OutputText.Text = split[0] switch
                {
                    "NG" => "ERROR: " + l,
                    "OK" => "SUCCESS: " + l,
                    _ => OutputText.Text
                };
            }
        }
        catch (Exception ex)
        {
            ShowError(ex.ToString());
            StatusLabel.Content = "An error occurred while reading error codes from UART. Please try again...";
        }
    }

    private static string CalculateChecksum(string str)
    {
        int sum = str.Aggregate(0, (current, c) => current + c);
        return str + ":" + (sum & 0xFF).ToString("X2");
    }

    private void ShowError(string error)
    {
        DialogHost.Show(new DialogContents(Dialog, error, "An error occurred.", "OK"), Dialog);
    }

    private void RefreshComButton_OnClick(object? sender, RoutedEventArgs e)
    {
        RefreshComPorts();
    }

    
    private async void Button_OnClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = GetTopLevel(this);

        var files = await topLevel!.StorageProvider.OpenFilePickerAsync(new()
        {
            Title = "Open NOR BIN File",
            AllowMultiple = false,
            FileTypeFilter = new List<FilePickerFileType>
            {
                new("PS5 BIN Files")
                {
                    Patterns = new List<string> { "*.bin" }
                }
            }
        });
        
        if (files.Count == 0)
            return;
        
        IStorageFile file = files[0];
        string norPath = file.Path.AbsolutePath;
        if (!File.Exists(norPath))
        {
            ShowError("The file you selected could not be found. Please check the file exists and is a valid BIN file.");
            return;
        }

        if(!file.Name.EndsWith(".bin", StringComparison.InvariantCultureIgnoreCase))
        {
            ShowError("You did not select a .bin file. Please ensure the file you are choosing is a correct BIN file and try again.");
            return;
        }

        StatusLabel.Content = "Status: Selected file " + norPath;
        NORDumpPath.Text = norPath;

        long length = new FileInfo(norPath).Length;
        FileSizeOut.Content = length + " bytes (" + length / 1024 / 1024 + "MB)";

        #region Extract PS5 Version

        SetData(norPath, Offsets.One, 12, out string? offsetOne, out _);
        SetData(norPath, Offsets.Two, 12, out string? offsetTwo, out _);
                        
        if(offsetOne?.Contains("22020101") ?? false)
        {
            PS5ModelOut.Content = "Disc Edition";
        }
        else
        {
            if(offsetTwo?.Contains("22030101") ?? false)
            {
                PS5ModelOut.Content = "Digital Edition";
            }
            else
            {
                PS5ModelOut.Content = "Unknown";
            }
        }

        #endregion

        #region Extract Motherboard Serial Number

        SetData(norPath, Offsets.MoboSerial, 16, out string? moboSerial, out string moboSerialText);

        MotherboardSerialOut.Content = moboSerial != null
            ? moboSerialText
            : "Unknown";

        #endregion

        #region Extract Board Serial Number

        SetData(norPath, Offsets.Serial, 17, out string? serial, out string serialText);
        
        if (serial != null)
        {
            SerialNumberOut.Content = serialText;
            SerialNumberIn.Text = serialText;
        }
        else
        {
            SerialNumberOut.Content = "Unknown";
            SerialNumberIn.Text = "";
        }

        #endregion

        #region Extract WiFi Mac Address

        SetData(norPath, Offsets.WiFiMAC, 6, out string? wiFiMAC, out _);
        if (wiFiMAC != null)
            wiFiMAC = string.Join("", wiFiMAC.Select((c, i) => i % 2 == 0 ? $"{c}" : $"{c}-"))[..^1];

        if (wiFiMAC != null)
        {
            WiFiMACAddressOut.Content = wiFiMAC;
            WiFiMACAddressIn.Text = wiFiMAC;
        }
        else
        {
            WiFiMACAddressOut.Content = "Unknown";
            WiFiMACAddressIn.Text = "";
        }

        #endregion

        #region Extract LAN Mac Address

        SetData(norPath, Offsets.LANMAC, 6, out string? lanmac, out _);
        if (lanmac != null)
            lanmac = string.Join("", lanmac.Select((c, i) => i % 2 == 0 ? $"{c}" : $"{c}-"))[..^1];

        if (lanmac != null)
        {
            LANMACAddressOut.Content = lanmac;
            LANMACAddressIn.Text = lanmac;
        }
        else
        {
            LANMACAddressOut.Content = "Unknown";
            LANMACAddressIn.Text = "";
        }

        #endregion

        #region Extract Board Variant
        
        SetData(norPath, Offsets.Variant, 19, out string? variant, out string variantText);
        if (variant != null)
            variant = variant.Replace("FF", null);

        variantText += " - " + Regions.GetValueOrDefault(variantText[^3..^1], "Unknown Region");

        BoardVariantOut.Content = variant != null ? variantText : "Unknown";

        #endregion

        return;
        
        void SetData(string path, long offset, int bytes, out string? dataValue, out string outputText)
        {
            try
            {
                BinaryReader reader = new(new FileStream(path, FileMode.Open));
                reader.BaseStream.Position = offset;

                byte[] dataBytes = reader.ReadBytes(bytes);
                dataValue = BitConverter.ToString(dataBytes).Replace("-", null);
                outputText = Encoding.ASCII.GetString(dataBytes);

                reader.Close();
            }
            catch
            {
                dataValue = null;
                outputText = "";
            }
        }
    }
}