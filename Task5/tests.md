# Набор интеграционных и end-to-end тестов для системы
|Название|Тип|Компоненты|Предусловия|
|:--|:-:|:--|:--| 
|Основной happy-path сценарий (Fraud Approve)|E2E|Payment Service, FraudCheck Service, Notification Service, Camunda, PostgreSQL, Redis|Сервисы запущены, Camunda деплоен, база и кэш доступны. У клиента на счете достаточно средств
|Сценарий с автоматическим отклонением транзакции (Fraud Reject)|E2E|Payment Service, FraudCheck Service, Notification Service, Camunda, PostgreSQL, Redis|Идентично предыдущим + FraudCheck Service настроен отклонить транзакцию |
|Сценарий ручного разрешения транзакции (Fraud Manual + Manual Approve)|E2E|Payment Service, FraudCheck Service, Notification Service, Camunda, PostgreSQL, Redis|Те же, FraudCheck настроен вызывать ручную проверку|
|Сценарий ручного отклонения транзакции (Fraud Manual + Manual Reject)|E2E|Payment Service, FraudCheck Service, Notification Service, Camunda, PostgreSQL, Redis|Те же|
|Ручная проверка с timeout 20 мин, автоматическое подтверждение |E2E|Payment Service, FraudCheck Service, Notification Service, Camunda, PostgreSQL, Redis|Те же. Оператор не принимает решение в течение 20 мин|
|Создание заказа со статусом «Ожидание оплаты» вместе со стартом процесса Camunda |Интеграционный|Payment Service, Camunda|Payment Service и Camunda подняты|
|Ошибка при попытке удержания средств, отмена заказа |Интеграционный|Payment Service, Camunda|Payment Service и Camunda подняты, Payment Service настроен вернуть ошибку при попытке удержания средств|
|Идемпотентность удержания средств |Интеграционный|Payment Service, PostgreSQL| Поднят Payment Service и база данных |
|Идемпотентность возврата средств |Интеграционный|Payment Service, PostgreSQL| Поднят Payment Service и база данных, ранее были зафиксированы средства на счете клиента |
|Возврат удержанных средств с уведомлением службы безопасности |Интеграционный|Payment Service, FraudCheck Service, Notification Service, Camunda|Сервисы подняты, процесс находится на этапе отклонения транзакции|
|Кэширование результата антифрод-проверки |Интеграционный|FraudCheck Service, Redis|Сервисы подняты, в кэше есть запись с отклоненной транзакцией (с тем же клиентом и контрагентом)|
|Компенсация при ошибке в середине Saga |Интеграционный|Payment Service, Camunda|На шаге fraud-check возникает техническая ошибка (сбой сервиса). Должен быть выполнен возврат удержанной суммы и отмена заказа|