namespace QuickSql.Tracking;

/// <summary>级联的落点：会话把"顺着这一根集合导航把子实体带进会话"实现成一颗泛型方法，调用它的那一处由元数据交出
/// （生成器写死或解析器启动期编译好同一个委托）。调用点必须握有编译期已知的子类型——会话自己拿 Type 去 MakeGenericType
/// 会破坏写侧 0 反射那条不变量。Enter 的实现只认两个类型参数，热路径上是一次普通接口调用，不是反射调度。</summary>
public interface ICascadeSink
{
    /// <summary>父实体这一根集合导航里装着的子实体，请进会话。children 为 null 说"集合位没装东西"：
    /// 插入档那是"没有要带的人"，删除档那是"捞不到子行"（会话据此拒，不留孤行）。
    /// memberName 是那根集合导航的属性名，只用于报错文本。</summary>
    void Enter<TParent, TChild>(TParent parent, IEnumerable<TChild>? children, string memberName)
        where TParent : class
        where TChild : class;
}
