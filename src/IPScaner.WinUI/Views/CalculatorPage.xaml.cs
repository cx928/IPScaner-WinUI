using System.Text;
using IPScaner.Core.Logging;
using IPScaner.Core.Net;
using IPScaner.WinUI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IPScaner.WinUI.Views;

/// <summary>
/// IP地址计算器 — the WinUI rebuild of the original <c>FormAddressCalc</c>
/// (IPScaner V1.28.2), backed by <see cref="SubnetCalculator"/>.
/// </summary>
/// <remarks>
/// Faithful to the original:
/// <list type="bullet">
/// <item>The exact captions: 网络和IP地址计算器, 显示网络，广播，第一个和最后一个给定的网络地址
/// and the 可用地址 / 掩码 / 网络 / 首个可用 / 末个可用 / 广播 output rows.</item>
/// <item>Every output is re-assigned on every 计算, so a failed calculation clears the
/// stale values of the previous one — exactly what <c>btnCalc_Click</c> did.</item>
/// <item>The engine's own markers are rendered verbatim: 错误, and the English
/// "two hosts" (/31) / "one host" (/32) strings. Errors never raise a dialog.</item>
/// </list>
/// Fixed / extended (documented deviations):
/// <list type="bullet">
/// <item>The original capped its mask input at 30, which made its own /31 and /32
/// branches unreachable dead code. This page offers the engine's full 0-32 range.</item>
/// <item>Invalid input can no longer throw: an emptied octet box is reported as 错误
/// by the engine instead of reaching <c>int.Parse</c>.</item>
/// <item>Extras that change no documented output: a CIDR line, the wildcard mask and
/// a 复制结果 button.</item>
/// </list>
/// </remarks>
public sealed partial class CalculatorPage : Page
{
    private const string PageTitle = "IP地址计算器";

    /// <summary>Clipboard text produced by the last calculation.</summary>
    private string _resultText = string.Empty;

    public CalculatorPage()
    {
        InitializeComponent();
        // The original left the outputs blank until 计算 was pressed; filling them
        // in on arrival costs nothing and makes the page self-explanatory.
        Loaded += (_, _) => Calculate();
    }

    // ---------------------------------------------------------------------
    // 计算
    // ---------------------------------------------------------------------

    private void OnCalcClick(object sender, RoutedEventArgs e) => Calculate();

    private void Calculate()
    {
        try
        {
            // NumberBox clamps typed input into 0..255 on its own; an emptied box is
            // NaN and is handed to the engine as -1 so it reports 错误 (the original
            // threw OverflowException/FormatException on such input).
            int[] octets = [ReadOctet(Ip1Box), ReadOctet(Ip2Box), ReadOctet(Ip3Box), ReadOctet(Ip4Box)];

            // 0..32: BitsFromMask also accepts "/24" and a dotted "255.255.255.0",
            // returning -1 for anything non-contiguous or out of range.
            var bits = SubnetCalculator.BitsFromMask(MaskBox.Text);
            var result = SubnetCalculator.Calculate(octets, bits);

            // Canonicalise a dotted mask into its bit count, exactly what the hint promises.
            if (bits >= 0) MaskBox.Text = bits.ToString();

            UsableBox.Text = result.UsableCount;
            SetOctets(Mask1Box, Mask2Box, Mask3Box, Mask4Box, result.Mask);
            SetOctets(Net1Box, Net2Box, Net3Box, Net4Box, result.Network);
            SetOctets(First1Box, First2Box, First3Box, First4Box, result.FirstUsable);
            SetOctets(Last1Box, Last2Box, Last3Box, Last4Box, result.LastUsable);
            SetOctets(Bcast1Box, Bcast2Box, Bcast3Box, Bcast4Box, result.Broadcast);

            // ---- extras ----------------------------------------------------
            var hasCidr = !result.HasError && result.Cidr.Length > 0;
            CidrText.Text = "CIDR：" + (hasCidr ? result.Cidr : "—");
            WildcardText.Text = "通配符掩码：" +
                (hasCidr && bits >= 0 ? SubnetCalculator.WildcardString(bits) : "—");

            _resultText = BuildResultText(result, bits);
            App.MainWindow?.SetStatus(hasCidr ? $"计算结果：{result.Cidr}" : "计算失败：请检查IP地址与掩码位");
        }
        catch (Exception ex)
        {
            AppLog.Instance.Log(nameof(CalculatorPage), "计算失败: " + ex.Message);
        }
    }

    /// <summary>One octet as an int, or -1 when the box is empty — never throws.</summary>
    private static int ReadOctet(NumberBox box)
    {
        var value = box.Value;
        if (double.IsNaN(value)) return -1;
        var octet = (int)Math.Round(value, MidpointRounding.AwayFromZero);
        return octet is >= 0 and <= 255 ? octet : -1;
    }

    /// <summary>
    /// Splits an engine field into the four output boxes. A non-dotted value (the
    /// literal 错误) lands in the first box with the rest blank, which is how the
    /// original's <c>txtSnm1.Text = "错误"</c> rendered.
    /// </summary>
    private static void SetOctets(TextBox box1, TextBox box2, TextBox box3, TextBox box4, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            box1.Text = string.Empty;
            box2.Text = string.Empty;
            box3.Text = string.Empty;
            box4.Text = string.Empty;
            return;
        }

        var parts = value.Split('.');
        box1.Text = parts.Length > 0 ? parts[0] : string.Empty;
        box2.Text = parts.Length > 1 ? parts[1] : string.Empty;
        box3.Text = parts.Length > 2 ? parts[2] : string.Empty;
        box4.Text = parts.Length > 3 ? parts[3] : string.Empty;
    }

    // ---------------------------------------------------------------------
    // 复制结果 (extra — not in the original)
    // ---------------------------------------------------------------------

    private async void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Calculate(); // keep the clipboard in step with the boxes
            if (string.IsNullOrEmpty(_resultText)) return;

            if (UiKit.CopyToClipboard(_resultText)) App.MainWindow?.SetStatus("计算结果已复制到剪贴板");
            else await UiKit.InfoAsync(XamlRoot, PageTitle, "复制失败：剪贴板不可用。");
        }
        catch (Exception ex)
        {
            AppLog.Instance.Log(nameof(CalculatorPage), "复制失败: " + ex.Message);
        }
    }

    private string BuildResultText(SubnetResult result, int bits)
    {
        var ip = $"{Ip1Box.Value:0}.{Ip2Box.Value:0}.{Ip3Box.Value:0}.{Ip4Box.Value:0}";
        var maskText = bits >= 0 ? bits.ToString() : MaskBox.Text.Trim();

        var text = new StringBuilder();
        text.AppendLine("网络和IP地址计算器");
        text.AppendLine($"IP/掩码位: {ip}/{maskText}");
        text.AppendLine($"可用地址: {UsableBox.Text}");
        text.AppendLine($"掩码: {Join(Mask1Box, Mask2Box, Mask3Box, Mask4Box)}");
        text.AppendLine($"网络: {Join(Net1Box, Net2Box, Net3Box, Net4Box)}");
        text.AppendLine($"首个可用: {Join(First1Box, First2Box, First3Box, First4Box)}");
        text.AppendLine($"末个可用：{Join(Last1Box, Last2Box, Last3Box, Last4Box)}");
        text.AppendLine($"广播: {Join(Bcast1Box, Bcast2Box, Bcast3Box, Bcast4Box)}");

        if (!result.HasError && result.Cidr.Length > 0)
        {
            text.AppendLine($"CIDR: {result.Cidr}");
            if (bits >= 0) text.AppendLine($"通配符掩码: {SubnetCalculator.WildcardString(bits)}");
        }
        return text.ToString().TrimEnd();
    }

    private static string Join(TextBox box1, TextBox box2, TextBox box3, TextBox box4) =>
        $"{box1.Text}.{box2.Text}.{box3.Text}.{box4.Text}";
}
