using System.Runtime.CompilerServices;
using System.Text;

namespace OpenUtau.Core.Util {
    /// <summary>
    /// 注册代码页编码（Shift-JIS / GB2312 / Big5 等）。
    ///
    /// .NET 只内置 Unicode 编码，非 Unicode 代码页需要 CodePagesEncodingProvider。
    /// 应用启动时 OpenUtau/Program.cs 会注册，但测试宿主、DAW 桥接等入口不经过它，
    /// 之前因此在 macOS 上抛 ArgumentException: 'shift_jis' is not a supported encoding name。
    /// 用模块初始化器在 OpenUtau.Core 载入时注册一次，所有入口都安全。
    /// </summary>
    internal static class EncodingSetup {
        [ModuleInitializer]
        internal static void Initialize() {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
    }
}
