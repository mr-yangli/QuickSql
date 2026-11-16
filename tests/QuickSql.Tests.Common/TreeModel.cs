using System.ComponentModel.DataAnnotations.Schema;
using QuickSql.Metadata;
using Da = System.ComponentModel.DataAnnotations;
using DaSchema = System.ComponentModel.DataAnnotations.Schema;

namespace QuickSql.TestInfra;

/// <summary>树形闭包用例的靶子实体。与级联那一家（<c>CascadeModel.cs</c>）同一条纪律：
/// 这些类型只被树形用例碰，没有哪一条既有用例枚举过它们，所以加实体不动别的黄金样本。
///
/// <para><b>为什么靶子要带 TenantId 与 Archived 两颗列</b>：整片那一档（<c>ToTree</c>）要求片界定，
/// 而"链上被筛掉的行会让它的子树整段读不到"这一格要有可筛的列才判得出来——
/// 前者用 TenantId 写 Where，后者用 Archived 注册全局过滤器，两格各钉一颗。</para></summary>
[Table("QuickSqlOrgNode")]
public class OrgNode
{
    /// <summary>键由调用方给（非自增）：树形用例要按确定的 id 摆父子关系，靠库回填就拿不到确定的靶子。
    /// 主键要显式声明：只写 IsIdentity=No 而让 Id 走命名约定的话，ApplyKeyConvention 会把约定自增
    /// OR 回去（EntityContractFactory:443），声明被盖掉——显式声明主键时那一段整个不跑（:188 的守卫）。</summary>
    [Da.Key]
    [QuickColumn(IsIdentity = TriState.No)]
    public int Id { get; set; }

    /// <summary>自引用父键。可空：null 就是根，这一颗是树形三颗口认的那颗列。</summary>
    public int? ParentId { get; set; }

    [QuickColumn(Length = 32)]
    public string? Name { get; set; }

    /// <summary>兄弟序：链上 OrderBy 点它，判"孩子的顺序 = 取数顺序，内存不重排"。</summary>
    public int SortNo { get; set; }

    public int TenantId { get; set; }

    /// <summary>全局过滤器的靶子：注册 !Archived 之后，被归档的中间节点会让它下面整段读不到。</summary>
    public bool Archived { get; set; }

    /// <summary>自引用的集合边。树形口<em>不用</em>它（父键从 lambda 现推，实体不必有孩子属性），
    /// 它在这里只为两格拒档作靶：父键 lambda 点到导航上（不是一颗列）、以及树口与 IncludeMany 叠链。</summary>
    [DaSchema.ForeignKey("ParentId")]
    public List<OrgNode>? Children { get; set; }
}

/// <summary>主键与父键不同一种值的拒档靶：<c>long</c> 主键配 <c>int</c> 父键。库里这样连得上，
/// 内存里两侧的值不是同一个 CLR 型 ⇒ 键表永远命不中 ⇒ 交回一棵"每行都是根"的扁表。</summary>
[Table("QuickSqlBadTypedNode")]
public class BadTypedNode
{
    [Da.Key]
    public long Id { get; set; }

    public int? ParentId { get; set; }

    [QuickColumn(Length = 32)]
    public string? Name { get; set; }
}

/// <summary>二进制键的靶：主键与父键都是 <c>byte[]</c>。这一族形状是键表比较器的试纸——
/// 物化每一趟都新建一只数组，按引用比的比较器会让每个节点都"找不到父"。</summary>
[Table("QuickSqlBinNode")]
public class BinNode
{
    [Da.Key]
    [QuickColumn(Length = 16)]
    public byte[]? Id { get; set; }

    [QuickColumn(Length = 16)]
    public byte[]? ParentId { get; set; }

    [QuickColumn(Length = 32)]
    public string? Name { get; set; }

    public int TenantId { get; set; }
}

/// <summary>字符串键的靶：用来钉规范文本那一行的锚点分账——
/// 一颗含分隔符的键与两颗键必须打出不同的文本，否则等价性判据自己漏了一类差异。</summary>
[Table("QuickSqlStrNode")]
public class StrNode
{
    [Da.Key]
    [QuickColumn(Length = 32)]
    public string? Id { get; set; }

    [QuickColumn(Length = 32)]
    public string? ParentId { get; set; }

    [QuickColumn(Length = 32)]
    public string? Name { get; set; }
}

/// <summary>复合主键的拒档靶：树形第一版只接单列主键（闭包的键集与父键臂都按一颗列发 IN，
/// 复合键的逐行 OR 链在层与层之间会放大成叉积）。这一颗存在的唯一理由是让那条拒档有东西可指。</summary>
[Table("QuickSqlOrgComposite")]
public class OrgCompositeNode
{
    [Da.Key]
    public int Id { get; set; }

    [Da.Key]
    public int TenantId { get; set; }

    public int? ParentId { get; set; }

    [QuickColumn(Length = 32)]
    public string? Name { get; set; }
}
