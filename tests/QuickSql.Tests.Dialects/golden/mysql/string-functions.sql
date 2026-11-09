
SELECT SUBSTRING(`t0`.`CustomerName` FROM @p0 + 1 FOR @p1) AS `Cut`, SUBSTRING(`t0`.`CustomerName` FROM @p2 + 1) AS `Tail`, (LOCATE(@p3, `t0`.`CustomerName`) - 1) AS `At`, CONCAT(IFNULL(CONCAT(IFNULL(`t0`.`CustomerName`, ''), IFNULL(@p4, '')), ''), IFNULL(`t0`.`Note`, '')) AS `Joined`, (`t0`.`Note` IS NULL OR TRIM(`t0`.`Note`) = '') AS `Blank` FROM `QuickSqlOrder` AS `t0`
