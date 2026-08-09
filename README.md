BionicPRO — проектная работа 9 спринта

Кейс BionicPRO: усиление безопасности системы и сервис отчётов о работе протеза.

| Задание | Содержание | Материалы |
|---|---|---|
| Задание 1 | Задача 1 — архитектура управления учётными данными (BFF, федерация УЗ, мультирегиональность) и диаграмма C4. Задача 2 — замена Code Grant на Code Grant + PKCE | [Task1/README.md](Task1/README.md), [Task1/BionicPRO_C4_container_to-be.drawio](Task1/BionicPRO_C4_container_to-be.drawio) |
| Задание 2 | Архитектура отчётности, ETL в Airflow (CRM + телеметрия → витрина в ClickHouse), сервис отчётов на C#, разграничение доступа, UI | [Task2/README.md](Task2/README.md), [Task2/BionicPRO_C4_reports_to-be.drawio](Task2/BionicPRO_C4_reports_to-be.drawio) |

## Состав стенда

| Каталог | Что это |
|---|---|
| [frontend/](frontend/) | Reports SPA: React 18 + TypeScript, вход через Keycloak с PKCE S256 |
| [reports-api/](reports-api/) | Сервис отчётов: C#, .NET 10, ASP.NET Core Minimal API |
| [airflow/dags/](airflow/dags/) | DAG `bionicpro_reports_etl` — подготовка витрины по расписанию |
| [clickhouse/init/](clickhouse/init/) | OLAP: схема, витрина, водяной знак, генератор телеметрии |
| [crm/init/](crm/init/) | Источник CRM (PostgreSQL): клиенты и протезы |
| [keycloak/](keycloak/) | Realm `reports-realm`: роли, пользователи, мапперы |

## Запуск

```bash
docker compose down -v
rm -rf postgres-keycloak-data   # realm импортируется только в пустую БД Keycloak (bind-mount, down -v его не чистит)
docker compose up -d --build

# наполнить витрину историей за 90 дней
docker compose exec airflow airflow dags trigger bionicpro_reports_etl --conf '{"backfill_days": 90}'
```

| Сервис | Адрес | Учётные данные |
|---|---|---|
| Reports SPA | http://localhost:3000 | `prothetic1 / prothetic123` (роль `prothetic_user`, клиент 1001)<br>`user1 / password123` (роль `user` — отчёты недоступны) |
| Reports API | http://localhost:8000 | JWT от Keycloak, `aud=reports-api` |
| Keycloak | http://localhost:8080 | `admin / admin` |
| Airflow | http://localhost:8081 | `admin / admin` |
| ClickHouse | http://localhost:8123 | `default` (запись), `reports_api` (только чтение) |

Порядок проверок с фактическими результатами — в разделах «Как проверить» ([Задание 1](Task1/README.md)) и «Проверка» ([Задание 2](Task2/README.md)).
