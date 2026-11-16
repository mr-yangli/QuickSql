using System.Linq.Expressions;
using System.Runtime.CompilerServices;

namespace QuickSql.Materialization;

internal static class ExpressionCompiler
{
    public static TDelegate CompileAotSafe<TDelegate>(this Expression<TDelegate> expression)
        where TDelegate : Delegate
        => RuntimeFeature.IsDynamicCodeSupported
            ? expression.Compile()
            : expression.Compile(preferInterpretation: true);
}
