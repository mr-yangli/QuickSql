
UPDATE `QuickSqlOrder` AS `t0` SET `Amount` = @p0 WHERE `t0`.`PaidAt` <> `t1`.`PaidAt` OR (`t0`.`PaidAt` IS NULL AND `t1`.`PaidAt` IS NOT NULL) OR (`t0`.`PaidAt` IS NOT NULL AND `t1`.`PaidAt` IS NULL)
