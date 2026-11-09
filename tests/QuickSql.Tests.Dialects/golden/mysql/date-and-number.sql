
SELECT DATE_ADD(`t0`.`CreatedAt`, INTERVAL (@p0) DAY) AS `Tomorrow`, GREATEST(`t0`.`Amount`, @p1) AS `Ceiling` FROM `QuickSqlOrder` AS `t0`
