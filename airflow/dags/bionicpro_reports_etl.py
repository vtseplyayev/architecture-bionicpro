"""
ETL витрины отчётности BionicPRO.

Источники:
  * CRM (PostgreSQL)          — измерения: клиенты и протезы;
  * ClickHouse telemetry_events — сырая телеметрия, её пишет Telemetry Gateway.

Результат:
  * bionic.report_daily_by_client — витрина «сутки × клиент», из которой
    Reports API отдаёт готовый отчёт без вычислений в реальном времени;
  * bionic.etl_watermark          — граница обработанного периода: API не
    отдаёт отчёт за даты, которых ещё нет в витрине.

Расписание: ежедневно в 02:00. Запуск в день D обрабатывает данные за D-1
(data_interval_start), поэтому водяной знак естественным образом отстаёт на сутки.

Ручной перерасчёт истории:
    Trigger DAG w/ config: {"backfill_days": 90}
"""

from __future__ import annotations

import json
import logging
import os
from datetime import datetime, timedelta

import pendulum
import requests
from airflow.decorators import dag, task
from airflow.exceptions import AirflowFailException

log = logging.getLogger(__name__)

CLICKHOUSE_URL = os.environ.get("CLICKHOUSE_URL", "http://clickhouse:8123/")
CRM_DSN = os.environ.get(
    "CRM_DSN", "host=crm_db port=5432 dbname=crm_db user=crm_user password=crm_password"
)
MART = "report_daily_by_client"
LATENCY_SLA_MS = 100  # целевое время реакции протеза


# --------------------------------------------------------------------------- #
# Вспомогательные функции
# --------------------------------------------------------------------------- #
def ch(query: str, body: bytes | None = None) -> str:
    """Выполнить запрос в ClickHouse через HTTP-интерфейс."""
    resp = requests.post(CLICKHOUSE_URL, params={"query": query}, data=body, timeout=300)
    if resp.status_code != 200:
        raise AirflowFailException(f"ClickHouse {resp.status_code}: {resp.text[:2000]}")
    return resp.text


def ch_insert_json(table: str, rows: list[dict]) -> None:
    """Загрузить строки в ClickHouse форматом JSONEachRow."""
    if not rows:
        log.warning("Нечего загружать в %s", table)
        return
    body = "\n".join(json.dumps(r, ensure_ascii=False, default=str) for r in rows).encode("utf-8")
    ch(f"INSERT INTO {table} FORMAT JSONEachRow", body)
    log.info("Загружено %s строк в %s", len(rows), table)


def crm_fetch(sql: str) -> list[dict]:
    """Извлечь данные из CRM (OLTP)."""
    import psycopg2
    import psycopg2.extras

    with psycopg2.connect(CRM_DSN) as conn:
        with conn.cursor(cursor_factory=psycopg2.extras.RealDictCursor) as cur:
            cur.execute(sql)
            return [dict(row) for row in cur.fetchall()]


def target_dates(context) -> list[str]:
    """
    Даты, за которые считаем витрину.
    По расписанию — один день (data_interval_start).
    При ручном запуске с backfill_days — последние N суток.
    """
    conf = (context["dag_run"].conf or {}) if context.get("dag_run") else {}
    base = context["data_interval_start"].date()
    days = int(conf.get("backfill_days", 0))
    if days > 0:
        return [(base - timedelta(days=i)).isoformat() for i in range(days)]
    return [base.isoformat()]


# --------------------------------------------------------------------------- #
# DAG
# --------------------------------------------------------------------------- #
@dag(
    dag_id="bionicpro_reports_etl",
    description="CRM + телеметрия → витрина отчётности в ClickHouse",
    schedule="0 2 * * *",
    start_date=pendulum.datetime(2026, 1, 1, tz="UTC"),
    catchup=False,
    max_active_runs=1,
    default_args={"retries": 2, "retry_delay": timedelta(minutes=5)},
    tags=["bionicpro", "etl", "reports"],
)
def bionicpro_reports_etl():

    @task
    def load_dim_clients() -> int:
        """
        E+L измерения «клиенты».
        В OLAP уезжает минимум ПДн: без email и номера договора — они для
        отчёта не нужны, а копия ПДн в аналитическом контуре — лишний риск.
        """
        rows = crm_fetch(
            """
            SELECT client_id, keycloak_username, full_name, city, contract_no, registered_at
            FROM clients
            """
        )
        loaded_at = datetime.utcnow().replace(microsecond=0).isoformat(sep=" ")
        for r in rows:
            r["registered_at"] = str(r["registered_at"])
            # contract_no маскируем: в отчёте показываем только последние 4 символа
            r["contract_no"] = "***" + str(r["contract_no"])[-4:]
            r["loaded_at"] = loaded_at
        ch_insert_json("bionic.dim_clients", rows)
        return len(rows)

    @task
    def load_dim_prostheses() -> int:
        """E+L измерения «протезы». telemetry_opt_in управляет попаданием в витрину."""
        rows = crm_fetch(
            """
            SELECT prosthesis_id, client_id, model, serial_number, side,
                   delivered_at, telemetry_opt_in
            FROM prostheses
            """
        )
        loaded_at = datetime.utcnow().replace(microsecond=0).isoformat(sep=" ")
        for r in rows:
            r["delivered_at"] = str(r["delivered_at"])
            r["telemetry_opt_in"] = 1 if r["telemetry_opt_in"] else 0
            r["loaded_at"] = loaded_at
        ch_insert_json("bionic.dim_prostheses", rows)
        return len(rows)

    @task
    def check_telemetry_ready(**context) -> list[str]:
        """
        Проверяем, что сырьё за целевые даты доехало. Дни без телеметрии
        отбрасываем: считать по ним витрину нечего, а водяной знак двигать
        по пустому дню — значит соврать API о готовности данных.
        """
        dates = target_dates(context)
        ready: list[str] = []
        for day in dates:
            cnt = int(
                ch(
                    "SELECT count() FROM bionic.telemetry_events "
                    f"WHERE toDate(event_time) = toDate('{day}')"
                ).strip()
            )
            log.info("Телеметрия за %s: %s событий", day, cnt)
            if cnt > 0:
                ready.append(day)
        if not ready:
            raise AirflowFailException(f"Нет телеметрии ни за одну из дат: {dates}")
        return sorted(ready)

    @task
    def build_mart(days: list[str], clients_loaded: int, prostheses_loaded: int) -> list[str]:
        """
        T+L: агрегация телеметрии в разрезе клиентов и джойн с измерениями CRM.
        Тяжёлая агрегация выполняется внутри ClickHouse (push-down), Airflow
        оркеструет — данные не гоняются через воркер.
        Идемпотентность: перед вставкой удаляем строки за пересчитываемую дату.
        """
        if clients_loaded == 0 or prostheses_loaded == 0:
            raise AirflowFailException("Измерения CRM не загружены — витрину строить не на чем")

        for day in days:
            ch(f"DELETE FROM bionic.{MART} WHERE report_date = toDate('{day}')")
            ch(
                f"""
                INSERT INTO bionic.{MART}
                (
                    client_id, report_date, full_name, city, prostheses_count,
                    events_total, gestures_recognized, recognition_rate,
                    avg_latency_ms, p95_latency_ms, max_latency_ms, slow_events,
                    battery_avg_pct, battery_min_pct, emg_quality_avg, errors_count,
                    active_hours, top_gesture,
                    latency_sum_ms, battery_sum_pct, emg_quality_sum, updated_at
                )
                SELECT
                    agg.client_id,
                    agg.report_date,
                    dc.full_name,
                    dc.city,
                    agg.prostheses_count,
                    agg.events_total,
                    agg.gestures_recognized,
                    agg.recognition_rate,
                    agg.avg_latency_ms,
                    agg.p95_latency_ms,
                    agg.max_latency_ms,
                    agg.slow_events,
                    agg.battery_avg_pct,
                    agg.battery_min_pct,
                    agg.emg_quality_avg,
                    agg.errors_count,
                    agg.active_hours,
                    agg.top_gesture,
                    agg.latency_sum_ms,
                    agg.battery_sum_pct,
                    agg.emg_quality_sum,
                    now() AS updated_at
                FROM
                (
                    SELECT
                        client_id,
                        toDate(event_time)                                AS report_date,
                        toUInt16(uniqExact(prosthesis_id))                AS prostheses_count,
                        count()                                           AS events_total,
                        sum(recognized)                                   AS gestures_recognized,
                        round(sum(recognized) / count(), 4)               AS recognition_rate,
                        round(avg(latency_ms), 2)                         AS avg_latency_ms,
                        round(quantile(0.95)(latency_ms), 2)              AS p95_latency_ms,
                        toUInt16(max(latency_ms))                         AS max_latency_ms,
                        countIf(latency_ms >= {LATENCY_SLA_MS})           AS slow_events,
                        round(avg(battery_pct), 2)                        AS battery_avg_pct,
                        toUInt8(min(battery_pct))                         AS battery_min_pct,
                        round(avg(emg_quality), 3)                        AS emg_quality_avg,
                        countIf(error_code != '')                         AS errors_count,
                        toUInt8(uniqExact(toHour(event_time)))            AS active_hours,
                        topK(1)(gesture)[1]                               AS top_gesture,
                        -- сырые суммы для взвешенного агрегата за период
                        sum(latency_ms)                                   AS latency_sum_ms,
                        sum(battery_pct)                                  AS battery_sum_pct,
                        sum(emg_quality)                                  AS emg_quality_sum
                    FROM bionic.telemetry_events AS e
                    INNER JOIN
                    (
                        SELECT prosthesis_id, client_id
                        FROM bionic.dim_prostheses FINAL
                        WHERE telemetry_opt_in = 1
                    ) AS dp USING (prosthesis_id)
                    WHERE toDate(e.event_time) = toDate('{day}')
                    GROUP BY client_id, report_date
                ) AS agg
                INNER JOIN
                (
                    SELECT client_id, full_name, city
                    FROM bionic.dim_clients FINAL
                ) AS dc USING (client_id)
                """
            )
            log.info("Витрина пересчитана за %s", day)
        return days

    @task
    def data_quality(days: list[str]) -> None:
        """Без этой проверки в витрину можно молча положить мусор."""
        checks = {
            "строк за обработанные даты": (
                "SELECT count() FROM bionic.{m} FINAL WHERE report_date IN ({d})", lambda v: v > 0
            ),
            "клиентов без идентификатора": (
                "SELECT count() FROM bionic.{m} FINAL WHERE client_id = 0", lambda v: v == 0
            ),
            "некорректная доля распознавания": (
                "SELECT count() FROM bionic.{m} FINAL "
                "WHERE recognition_rate < 0 OR recognition_rate > 1", lambda v: v == 0
            ),
            "распознано больше, чем событий": (
                "SELECT count() FROM bionic.{m} FINAL "
                "WHERE gestures_recognized > events_total", lambda v: v == 0
            ),
        }
        dates_in = ", ".join(f"toDate('{d}')" for d in days)
        for name, (sql, ok) in checks.items():
            value = int(ch(sql.format(m=MART, d=dates_in)).strip())
            log.info("DQ «%s» = %s", name, value)
            if not ok(value):
                raise AirflowFailException(f"Data quality: проверка «{name}» не прошла (={value})")

    @task
    def update_watermark() -> str:
        """
        Двигаем границу готовности. Reports API читает её и не отдаёт отчёт
        за период, который ещё не посчитан, — вместо пустого отчёта пользователь
        получает честный ответ «данные ещё не готовы».
        """
        ch(
            f"""
            INSERT INTO bionic.etl_watermark
            SELECT '{MART}', min(report_date), max(report_date), now()
            FROM bionic.{MART} FINAL
            """
        )
        state = ch(
            "SELECT min_ready_date, max_ready_date FROM bionic.etl_watermark FINAL "
            f"WHERE mart = '{MART}'"
        ).strip()
        log.info("Водяной знак: %s", state)
        return state

    clients = load_dim_clients()
    prostheses = load_dim_prostheses()
    ready_days = check_telemetry_ready()
    built = build_mart(ready_days, clients, prostheses)
    data_quality(built) >> update_watermark()


bionicpro_reports_etl()
