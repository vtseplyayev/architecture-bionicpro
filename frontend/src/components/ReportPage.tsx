import React, { useCallback, useEffect, useState } from 'react';
import { useKeycloak } from '@react-keycloak/web';

const API_URL = process.env.REACT_APP_API_URL || 'http://localhost:8000';
const REQUIRED_ROLE = 'prothetic_user';

interface DailyRow {
  report_date: string;
  prostheses_count: number;
  events_total: number;
  gestures_recognized: number;
  recognition_rate: number;
  avg_latency_ms: number;
  p95_latency_ms: number;
  max_latency_ms: number;
  slow_events: number;
  battery_avg_pct: number;
  battery_min_pct: number;
  emg_quality_avg: number;
  errors_count: number;
  active_hours: number;
  top_gesture: string;
}

interface ReportResponse {
  client: { client_id: number; full_name: string; city: string };
  period: {
    from: string;
    to: string;
    requested_from: string;
    requested_to: string;
    clamped: boolean;
    note: string | null;
  };
  data_ready_through: string;
  summary: {
    days: number;
    events_total: number;
    gestures_recognized: number;
    recognition_rate: number | null;
    avg_latency_ms: number | null;
    worst_p95_latency_ms: number | null;
    slow_events: number;
    slow_events_share: number | null;
    battery_avg_pct: number | null;
    battery_min_pct: number | null;
    emg_quality_avg: number | null;
    errors_count: number;
    active_hours_avg: number | null;
    prostheses_count: number;
    top_gesture: string;
  };
  daily: DailyRow[];
  generated_at: string;
}

interface Meta {
  data_ready_from: string;
  data_ready_through: string;
  mart_updated_at: string;
}

const PERIODS = [
  { label: '7 дней', days: 7 },
  { label: '30 дней', days: 30 },
  { label: '90 дней', days: 90 }
];

const percent = (value: number | null): string =>
  value === null || value === undefined ? '—' : `${(value * 100).toFixed(1)} %`;

const number = (value: number | null, digits = 0): string =>
  value === null || value === undefined ? '—' : value.toFixed(digits);

const ReportPage: React.FC = () => {
  const { keycloak, initialized } = useKeycloak();
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [report, setReport] = useState<ReportResponse | null>(null);
  const [meta, setMeta] = useState<Meta | null>(null);
  const [days, setDays] = useState(30);

  const hasRole = keycloak?.tokenParsed
    ? (keycloak.tokenParsed as any)?.realm_access?.roles?.includes(REQUIRED_ROLE) === true
    : false;

  /** Один вызов API: обновляем токен, если он вот-вот протухнет, и шлём Bearer. */
  const callApi = useCallback(
    async (path: string): Promise<Response> => {
      await keycloak.updateToken(30).catch(() => keycloak.login());
      return fetch(`${API_URL}${path}`, {
        headers: { Authorization: `Bearer ${keycloak.token}` }
      });
    },
    [keycloak]
  );

  /** Читаемое сообщение вместо «An error occurred». */
  const describeError = async (response: Response): Promise<string> => {
    let detail = '';
    try {
      const problem = await response.json();
      detail = problem.detail || problem.title || '';
    } catch {
      /* тело не ProblemDetails — обойдёмся кодом */
    }
    switch (response.status) {
      case 401:
        return 'Сессия истекла — войдите заново.';
      case 403:
        return detail || `Доступ запрещён: нужна роль ${REQUIRED_ROLE}.`;
      case 409:
        return detail || 'Запрошенный период ещё не обработан Airflow.';
      case 503:
        return detail || 'Витрина отчётности ещё не построена.';
      default:
        return detail || `Ошибка сервиса отчётов (HTTP ${response.status}).`;
    }
  };

  // Границу готовности данных показываем сразу: пользователь должен видеть,
  // по какую дату отчёт вообще может быть построен.
  useEffect(() => {
    if (!initialized || !keycloak.authenticated || !hasRole) return;
    callApi('/reports/meta')
      .then(async (response) => {
        if (response.ok) setMeta(await response.json());
      })
      .catch(() => undefined);
  }, [initialized, keycloak.authenticated, hasRole, callApi]);

  const downloadReport = async () => {
    try {
      setLoading(true);
      setError(null);

      const to = meta?.data_ready_through;
      const query = new URLSearchParams();
      if (to) {
        const toDate = new Date(to);
        const fromDate = new Date(toDate);
        fromDate.setDate(fromDate.getDate() - (days - 1));
        query.set('from', fromDate.toISOString().slice(0, 10));
        query.set('to', to);
      }

      const response = await callApi(`/reports?${query.toString()}`);
      if (!response.ok) {
        setReport(null);
        setError(await describeError(response));
        return;
      }
      setReport(await response.json());
    } catch (err) {
      setReport(null);
      setError(err instanceof Error ? err.message : 'Не удалось получить отчёт');
    } finally {
      setLoading(false);
    }
  };

  const saveAsJson = () => {
    if (!report) return;
    const blob = new Blob([JSON.stringify(report, null, 2)], { type: 'application/json' });
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = `bionicpro-report-${report.period.from}_${report.period.to}.json`;
    link.click();
    URL.revokeObjectURL(url);
  };

  if (!initialized) {
    return <div className="p-8">Загрузка…</div>;
  }

  if (!keycloak.authenticated) {
    return (
      <div className="flex flex-col items-center justify-center min-h-screen bg-gray-100">
        <h1 className="text-2xl font-bold mb-6">BionicPRO — отчёты о работе протеза</h1>
        <button
          onClick={() => keycloak.login()}
          className="px-4 py-2 bg-blue-500 text-white rounded hover:bg-blue-600"
        >
          Войти
        </button>
      </div>
    );
  }

  return (
    <div className="min-h-screen bg-gray-100 p-6">
      <div className="max-w-6xl mx-auto">
        <header className="flex items-center justify-between mb-6">
          <div>
            <h1 className="text-2xl font-bold">Отчёт о работе протеза</h1>
            <p className="text-sm text-gray-600">
              {keycloak.tokenParsed?.preferred_username}
              {meta && ` · данные готовы по ${meta.data_ready_through}`}
            </p>
          </div>
          <button
            onClick={() => keycloak.logout()}
            className="px-3 py-2 text-sm bg-gray-200 rounded hover:bg-gray-300"
          >
            Выйти
          </button>
        </header>

        {!hasRole && (
          <div className="p-4 bg-yellow-100 text-yellow-800 rounded">
            У учётной записи нет роли <b>{REQUIRED_ROLE}</b> — отчёты недоступны.
          </div>
        )}

        {hasRole && (
          <div className="p-6 bg-white rounded-lg shadow-md">
            <div className="flex flex-wrap items-center gap-3 mb-4">
              <label className="text-sm text-gray-700">Период:</label>
              <select
                value={days}
                onChange={(event) => setDays(Number(event.target.value))}
                className="px-3 py-2 border rounded"
              >
                {PERIODS.map((period) => (
                  <option key={period.days} value={period.days}>
                    {period.label}
                  </option>
                ))}
              </select>

              <button
                onClick={downloadReport}
                disabled={loading}
                className={`px-4 py-2 bg-blue-500 text-white rounded hover:bg-blue-600 ${
                  loading ? 'opacity-50 cursor-not-allowed' : ''
                }`}
              >
                {loading ? 'Формируем отчёт…' : 'Получить отчёт'}
              </button>

              {report && (
                <button
                  onClick={saveAsJson}
                  className="px-4 py-2 bg-green-600 text-white rounded hover:bg-green-700"
                >
                  Скачать JSON
                </button>
              )}
            </div>

            {error && <div className="mt-2 p-4 bg-red-100 text-red-700 rounded">{error}</div>}

            {report && (
              <>
                <div className="mb-4 text-sm text-gray-700">
                  <b>{report.client.full_name || `Клиент ${report.client.client_id}`}</b>
                  {report.client.city && `, ${report.client.city}`} · период{' '}
                  {report.period.from} — {report.period.to} · протезов:{' '}
                  {report.summary.prostheses_count}
                </div>

                {report.period.clamped && report.period.note && (
                  <div className="mb-4 p-3 bg-blue-50 text-blue-800 rounded text-sm">
                    {report.period.note}
                  </div>
                )}

                <div className="grid grid-cols-2 md:grid-cols-4 gap-3 mb-6">
                  <Stat title="Событий" value={report.summary.events_total.toLocaleString('ru-RU')} />
                  <Stat title="Распознавание" value={percent(report.summary.recognition_rate)} />
                  <Stat
                    title="Средняя задержка"
                    value={`${number(report.summary.avg_latency_ms, 1)} мс`}
                  />
                  <Stat
                    title="Медленнее 100 мс"
                    value={percent(report.summary.slow_events_share)}
                  />
                  <Stat
                    title="Худший p95"
                    value={`${number(report.summary.worst_p95_latency_ms, 1)} мс`}
                  />
                  <Stat title="Батарея, средняя" value={`${number(report.summary.battery_avg_pct, 1)} %`} />
                  <Stat title="Ошибок" value={report.summary.errors_count.toLocaleString('ru-RU')} />
                  <Stat title="Частый жест" value={report.summary.top_gesture || '—'} />
                </div>

                <div className="overflow-x-auto">
                  <table className="min-w-full text-sm">
                    <thead className="bg-gray-100 text-left">
                      <tr>
                        <th className="p-2">Дата</th>
                        <th className="p-2">Событий</th>
                        <th className="p-2">Распознано</th>
                        <th className="p-2">Ср. задержка</th>
                        <th className="p-2">p95</th>
                        <th className="p-2">&ge; 100 мс</th>
                        <th className="p-2">Батарея мин.</th>
                        <th className="p-2">Ошибок</th>
                        <th className="p-2">Часов активности</th>
                        <th className="p-2">Частый жест</th>
                      </tr>
                    </thead>
                    <tbody>
                      {report.daily.map((row) => (
                        <tr key={row.report_date} className="border-b">
                          <td className="p-2">{row.report_date}</td>
                          <td className="p-2">{row.events_total}</td>
                          <td className="p-2">{percent(row.recognition_rate)}</td>
                          <td className="p-2">{number(row.avg_latency_ms, 1)} мс</td>
                          <td className="p-2">{number(row.p95_latency_ms, 1)} мс</td>
                          <td className="p-2">{row.slow_events}</td>
                          <td className="p-2">{row.battery_min_pct} %</td>
                          <td className="p-2">{row.errors_count}</td>
                          <td className="p-2">{row.active_hours}</td>
                          <td className="p-2">{row.top_gesture}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              </>
            )}
          </div>
        )}
      </div>
    </div>
  );
};

const Stat: React.FC<{ title: string; value: string }> = ({ title, value }) => (
  <div className="p-3 bg-gray-50 rounded border">
    <div className="text-xs text-gray-500">{title}</div>
    <div className="text-lg font-semibold">{value}</div>
  </div>
);

export default ReportPage;
