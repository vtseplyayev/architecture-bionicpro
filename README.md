BionicPRO — проектная работа 9 спринта

Кейс BionicPRO: усиление безопасности системы и реализация отчётов о работе протеза.

| Задание | Содержание | Материалы |
|---|---|---|
| Задание 1 | Задача 1 — архитектурное решение по управлению учётными данными (BFF, федерация УЗ, мультирегиональность) и доработанная диаграмма C4. Задача 2 — замена Authorization Code Grant на Code Grant + PKCE | [Task1/README.md](Task1/README.md), [Task1/BionicPRO_C4_container_to-be.drawio](Task1/BionicPRO_C4_container_to-be.drawio) |

## Запуск стенда

```bash
docker compose down -v
rm -rf postgres-keycloak-data   # realm импортируется только в пустую БД Keycloak (bind-mount, down -v его не чистит)
docker compose up -d --build
```

| Сервис | Адрес | Учётные данные |
|---|---|---|
| Frontend (Reports SPA) | http://localhost:3000 | `prothetic1 / prothetic123` (роль `prothetic_user`)<br>`user1 / password123` (роль `user`) |
| Keycloak | http://localhost:8080 | `admin / admin` |

Realm: `reports-realm`, клиенты `reports-frontend` (public, PKCE S256 обязателен) и `reports-api` (bearer-only).
Порядок проверки PKCE — в разделе «Как проверить» файла [Task1/README.md](Task1/README.md).
