
SELECT `t0`.`Id`, `t0`.`CustomerName`, `t0`.`CustomerId`, `t0`.`Amount`, `t0`.`State`, `t0`.`CreatedAt`, `t0`.`PaidAt`, `t0`.`Note` FROM `QuickSqlOrder` AS `t0` WHERE CHAR_LENGTH(`t0`.`CustomerName`) > @p0 AND EXTRACT(year FROM `t0`.`CreatedAt`) = @p1 AND ROUND(`t0`.`Amount`, @p2) > @p3
