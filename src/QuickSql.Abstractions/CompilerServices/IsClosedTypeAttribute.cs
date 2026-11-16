// C# 15 `closed` 层级的 net10.0 档脚手架：编译器只认元数据名 System.Runtime.CompilerServices.IsClosedTypeAttribute，
// net11.0 运行时自带这一颗、net10.0 不带 —— 按元数据名就地补齐（与 IsByRefLikeAttribute 等 polyfill 同一手法）。
// internal：不进公开面；net11.0 资产不含本文件，两档产出的 AST 元数据对消费方同形，不需要 #if 以外的任何分叉。
#if !NET11_0_OR_GREATER
namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    internal sealed class IsClosedTypeAttribute : Attribute
    {
        public IsClosedTypeAttribute() { }
    }
}
#endif
