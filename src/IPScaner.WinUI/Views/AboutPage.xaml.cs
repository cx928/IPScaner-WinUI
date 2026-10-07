using System.Reflection;
using IPScaner.Core.Configuration;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IPScaner.WinUI.Views;

/// <summary>
/// 关于 — version, runtime facts, contact details and the release history carried
/// over from the original readme.
/// </summary>
public sealed partial class AboutPage : Page
{
    /// <summary>Project website, also stamped into the MSI's support link.</summary>
    public const string Website = "https://www.xiaorin.cn";

    /// <summary>Contact address shown here and in the installer's privacy policy.</summary>
    public const string ContactEmail = "wanghaotian@cxdx.deu.kg";

    /// <summary>Source repository.</summary>
    public const string Repository = "https://github.com/cx928/IPScaner-WinUI";

    /// <summary>Condensed release notes, transcribed from the original readme.</summary>
    private const string Changelog = """
        版本1.28：优化检测IP在线的机制，修复部分电脑禁PING导致显示不在线的问题；
        新增【PING失败通过ARP表检查在线】选项（默认关闭）；新增【最小化时隐藏到托盘】选项（默认关闭）。
        版本1.27：端口扫描结果可复制到剪贴板；增加MAC备注功能，IP与MAC同时存在时优先使用MAC。
        版本1.26：端口扫描支持掩码位IP与单IP指定端口；新增IP异动监测（上线/下线）；修改本地IP保存历史记录。
        版本1.25：修复ARP读取MAC地址不准确的问题；桌面显示支持颜色、背景色、透明度与掩码/网关/DNS。
        版本1.24：支持在桌面四个角显示本机IP；部分功能增加调试日志。
        版本1.23：IP批量扫描支持点击列头排序。      版本1.22：IP批量扫描增加主机名、MAC列。
        版本1.21：修改本地IP支持DHCP与静态两种方式，并增加DNS设置。
        版本1.20：修复Win11 24H2不可查看WiFi密码；Ping失败时尝试侦测特定端口；增加备注管理。
        版本1.19：小色块右键增加访问共享目录；支持按掩码位、按IP范围批量扫描。
        版本1.18：端口占用双击PID可结束进程；色块右键支持浏览网页；颜色与双击事件可自定义。
        版本1.17：增加WiFi密码查看器；增加用户备注；端口查看支持反向筛选；增加结果导出。
        版本1.16：增加系统端口查看器。              版本1.15：增加IP地址计算器。
        版本1.14：色块右键增加快速端口扫描。        版本1.13：色块右键增加常用命令。
        版本1.12：顶部IP扫描段支持多网卡IP。        版本1.11：IP段扫描改为多网卡显示。
        版本1.10：点击图例可批量复制导出。          版本1.9：增加Windows工具箱。
        """;

    public AboutPage()
    {
        InitializeComponent();

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.28.2";
        VersionLine.Text = $"WinUI 重构版 {version}    ·    原版 IPScaner V1.28.2";
        ChangelogText.Text = Changelog;

        var services = Services.AppServices.Current;
        EnvironmentLine.Text =
            $"操作系统：{Environment.OSVersion.VersionString}    ·    " +
            $".NET：{Environment.Version}    ·    UI：Windows App SDK / WinUI 3";
        PathLine.Text =
            $"配置文件：{services.ConfigStore.FilePath}\n" +
            $"数据目录：{IPScaner.Core.Storage.AppPaths.DataDirectory}" +
            (IPScaner.Core.Storage.AppPaths.IsPortable ? "（免安装模式：数据随程序目录）" : "（安装模式：数据在用户配置目录）");
        ElevationLine.Text = services.IsElevated
            ? "当前进程：管理员权限（修改本地IP、清空ARP缓存均可用）"
            : "当前进程：普通权限（修改本地IP、清空ARP缓存需要以管理员身份重启）";

        SiteLink.Content = Website;
        MailLink.Content = ContactEmail;
        RepoLine.Text = $"开源仓库：{Repository}";
    }

    private void OnOpenSite(object sender, RoutedEventArgs e) =>
        Services.AppServices.Current.Shell.OpenUrl(Website);

    private void OnOpenMail(object sender, RoutedEventArgs e) =>
        Services.AppServices.Current.Shell.OpenUrl("mailto:" + ContactEmail);

    private async void OnCopySite(object sender, RoutedEventArgs e)
    {
        Services.UiKit.CopyToClipboard(Website);
        await Services.UiKit.InfoAsync(XamlRoot, "关于", $"已复制网址：{Website}");
    }

    private async void OnCopyMail(object sender, RoutedEventArgs e)
    {
        Services.UiKit.CopyToClipboard(ContactEmail);
        await Services.UiKit.InfoAsync(XamlRoot, "关于", $"已复制邮箱：{ContactEmail}");
    }
}
