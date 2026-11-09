### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
QuickSql001 | QuickSql.Metadata | Error | 同一层元数据来源互斥，对应反射侧的 MetadataConflictException（§10.2）
QuickSql002 | QuickSql.Metadata | Error | 索引引用了未映射的属性，对应 ContractResolver 的同一句失败
QuickSql003 | QuickSql.Metadata | Info | 该实体不走 G1，运行期退回 G2 反射解析（§12.7 A）
QuickSql004 | QuickSql.Metadata | Error | [ForeignKey] 指向了未映射的属性，对应 EntityContractFactory 收尾时的那句启动期失败
QuickSql005 | QuickSql.Metadata | Error | 跳过索引声明不完整（缺表达式或成员不在列清单），话术与 EntityContractFactory.MapSkipIndexes 逐字相同
