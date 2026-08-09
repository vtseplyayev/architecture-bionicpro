import React from 'react';
import { ReactKeycloakProvider } from '@react-keycloak/web';
import Keycloak, { KeycloakConfig, KeycloakInitOptions } from 'keycloak-js';
import ReportPage from './components/ReportPage';

const keycloakConfig: KeycloakConfig = {
  url: process.env.REACT_APP_KEYCLOAK_URL,
  realm: process.env.REACT_APP_KEYCLOAK_REALM||"",
  clientId: process.env.REACT_APP_KEYCLOAK_CLIENT_ID||""
};

const keycloak = new Keycloak(keycloakConfig);

// Задание 1, Задача 2: Authorization Code Grant + PKCE (RFC 7636).
// pkceMethod: 'S256' заставляет адаптер сгенерировать code_verifier, передать
// code_challenge = BASE64URL(SHA-256(code_verifier)) в /auth и code_verifier в /token.
// Перехваченный authorization code без code_verifier бесполезен.
const keycloakInitOptions: KeycloakInitOptions = {
  onLoad: 'check-sso',
  pkceMethod: 'S256',
  // Проверка существующей SSO-сессии в скрытом iframe вместо редиректа всей страницы.
  silentCheckSsoRedirectUri: `${window.location.origin}/silent-check-sso.html`,
  checkLoginIframe: false,
  // Токен не попадает в адресную строку и историю браузера.
  responseMode: 'query'
};

const App: React.FC = () => {
  return (
    <ReactKeycloakProvider authClient={keycloak} initOptions={keycloakInitOptions}>
      <div className="App">
        <ReportPage />
      </div>
    </ReactKeycloakProvider>
  );
};

export default App;
