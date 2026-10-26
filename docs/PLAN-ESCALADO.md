# Plan para escalar OwnIDM a producto

Punto de partida (octubre 2026): aplicación WPF (.NET 8) funcional con motor multi-conexión, HLS/DASH,
yt-dlp, programador, grabber, extensión MV3 + native messaging e instalador Inno Setup. 74 tests xUnit
con servidor HTTP local. **Faltan los cimientos de producto**: no hay control de versiones, ni CI, ni
firma de código, ni actualización automática de la propia app, ni forma de saber qué falla en los
equipos de los usuarios. El plan ataca eso primero: sin ello, cada función nueva aumenta el riesgo.

---

## 1. Cómo es cada ciclo

Ciclos de **2 semanas**, siempre con la misma estructura. Ninguna función se da por terminada
fuera del ciclo de pruebas.

| Días | Fase | Qué se hace | Salida |
|---|---|---|---|
| 1 | Planificación | Elegir 3–5 objetivos del ciclo; escribir para cada uno el criterio de aceptación y cómo se probará | Lista del ciclo con criterios |
| 2–7 | Desarrollo | Rama por objetivo, tests unitarios/integración junto al código, PR revisado | PRs con CI en verde |
| 8–9 | Congelación + pruebas | Nada nuevo entra. Matriz de pruebas manuales (§3) sobre la build candidata | Informe de pruebas, bugs triados |
| 10 | Beta | Build firmada al canal **beta** (usuarios voluntarios) | Versión `x.y.0-beta.n` |
| 11–13 | Estabilización | Solo arreglos de bugs de beta; se monitorizan los indicadores (§4) | Bugs P0/P1 a cero |
| 14 | Publicación + retro | Paso a **estable** si se cumplen los criterios de salida; retrospectiva de 30 min | Versión `x.y.0`, notas de versión |

**Definición de terminado** (para cada objetivo): código revisado · tests automáticos nuevos ·
CI en verde · probado en la matriz manual que le aplique · documentado (README / notas de versión) ·
sin regresiones en los indicadores de la beta.

**Severidades**: P0 = pérdida de datos, archivo corrupto o cuelgue (bloquea la publicación) · P1 =
función principal rota sin alternativa (bloquea) · P2 = rota con alternativa (se publica con aviso) ·
P3 = cosmético.

---

## 2. Hoja de ruta por ciclos

### Ciclo 0 — Cimientos (antes de cualquier función nueva)

*Desarrollo*
- `git init`, repositorio privado en GitHub, ramas `main` (estable) + ramas de trabajo, PR obligatorio.
- CI con GitHub Actions en `windows-latest`: `dotnet build -c Release`, `dotnet test`, análisis de
  avisos como errores, y artefacto del instalador por cada commit en `main`.
- Versionado semántico único (una sola fuente para la app, el host, la extensión y el instalador).
- Un job nocturno con yt-dlp, ffmpeg y Deno reales y `OWNIDM_REQUIRE_YTDLP=1`, para que el test de
  integración de yt-dlp **se ejecute de verdad** (hoy se salta si no hay yt-dlp).

*Pruebas*
- La CI debe fallar a propósito una vez (test roto) para comprobar que bloquea el merge.
- Instalar el artefacto de la CI en una VM limpia de Windows 10 y otra de Windows 11.

*Criterio de salida*: cada cambio pasa por PR con CI en verde y produce un instalador.

### Ciclo 1 — Estabilizar el vídeo (YouTube y streams)

Es lo que más usan y lo que más se rompe (YouTube cambia a menudo).

*Desarrollo*
- Validar en real los arreglos de esta versión: datos de la página reutilizados al iniciar, tamaño
  exacto por calidad, total estable durante la descarga, «Reanudable: sí».
- Instalar la build *onedir* de yt-dlp (`yt-dlp_win.zip`) en lugar del `.exe` de un solo archivo: este
  se descomprime en cada ejecución y añade 1–3 s a cada arranque (más con el antivirus).
- Mensajes de error accionables (bot check → «inicia sesión en el navegador», vídeo privado, región).
- Registro local rotativo (`%LOCALAPPDATA%\OwnIDM\logs`) con la salida de yt-dlp/ffmpeg.

*Pruebas*
- **Batería de vídeos de referencia** (20–30 URL fijas): vídeo normal, Short vertical, 4K/60 fps,
  solo audio, con varias pistas de idioma, con restricción de edad, directo (debe rechazarse),
  lista de reproducción (debe rechazarse), y 5 sitios no-YouTube. Se ejecuta en el job nocturno y
  registra: tiempo hasta el primer byte, tamaño mostrado vs. tamaño final, éxito/fallo.
- Pausar/reanudar en mitad del vídeo, en mitad del audio y tras reiniciar el PC.
- Cola con 5 vídeos y límite de velocidad.

*Criterio de salida*: ≥ 95 % de éxito en la batería; tamaño mostrado = final ±1 % cuando se marca
como exacto; inicio de la descarga < 3 s tras pulsar «Iniciar».

### Ciclo 2 — Distribución confiable

*Desarrollo*
- **Firma de código** (certificado OV/EV o Azure Trusted Signing) del `.exe`, el host y el instalador:
  sin firma, SmartScreen asusta y bloquea a los usuarios nuevos.
- **Actualización automática de OwnIDM** (no solo de yt-dlp): comprobación diaria contra un manifiesto
  firmado, descarga con el propio motor, verificación SHA-256 + firma, instalación al cerrar.
- Canales `estable` y `beta` seleccionables en Opciones.
- Publicar la extensión: Edge Add-ons y Chrome Web Store. **Ojo**: la política de Chrome Web Store
  prohíbe extensiones que descarguen de YouTube; preparar una variante de la extensión sin esa parte
  para Chrome o dejarla solo en Edge / instalación manual.
- Paquete `winget` del instalador.

*Pruebas*
- Actualizar de cada una de las 3 últimas versiones a la nueva, con descargas en pausa: deben
  conservarse y reanudarse.
- Actualización interrumpida (sin red, PC apagado a mitad): la app sigue arrancando.
- Instalación en una cuenta sin privilegios y con SmartScreen activo.

*Criterio de salida*: un usuario de la beta recibe la versión siguiente sin intervenir.

### Ciclo 3 — Observabilidad y soporte

*Desarrollo*
- Informes de fallos **voluntarios** (opt-in explícito): excepción no controlada + versión + SO, sin
  URL ni nombres de archivo. Sentry o similar.
- Métricas anónimas opt-in: descargas iniciadas/completadas/fallidas por tipo (HTTP, HLS, DASH, yt-dlp).
- «Ayuda → Exportar diagnóstico»: zip con logs y ajustes (sin cookies) para adjuntar a una incidencia.
- Plantillas de incidencia en GitHub y página de preguntas frecuentes.

*Pruebas*
- Forzar fallos (excepción en el motor, disco lleno, permisos denegados) y comprobar que el informe
  llega y que **no** contiene datos personales.
- Revisión de privacidad: qué se envía, con qué consentimiento, cómo se desactiva.

*Criterio de salida*: indicadores de §4 visibles en un panel para la beta.

### Ciclo 4 — Calidad automatizada de la interfaz y rendimiento

*Desarrollo*
- Tests de interfaz con **FlaUI** (UI Automation) de los flujos críticos: añadir URL, diálogo de
  información con calidades, pausar/reanudar, cola, opciones.
- Virtualización de la lista de descargas y guardado incremental de `downloads.json` (hoy se reescribe
  entero cada 5 s mientras hay descargas).
- Perfilado del motor con 32 conexiones y archivos > 10 GB.

*Pruebas*
- Estrés: 1 000 elementos en la lista, 10 descargas simultáneas, 24 h seguidas sin fugas de memoria.
- Red adversa (latencia alta, cortes, servidor que ignora `Range`), con un proxy de pruebas tipo toxiproxy.

*Criterio de salida*: flujos críticos cubiertos por tests de UI en la CI; uso de memoria estable en 24 h.

### Ciclo 5 — Funciones pendientes respecto a IDM

Por orden de demanda probable: proxy manual (HTTP/SOCKS) · autenticación HTTP por sitio · FTP ·
proyectos guardados y programación del grabber · DASH con varios periodos.

*Pruebas*: cada función con su test de integración contra el servidor local (ampliado con proxy,
autenticación y FTP de pruebas) y su fila nueva en la matriz manual.

### Ciclo 6 — Producto para más usuarios

- Interfaz en inglés (recursos `.resx`), además de español.
- Accesibilidad: navegación con teclado, lector de pantalla, alto contraste.
- Web del producto, documentación de usuario y política de privacidad.
- Revisión legal: licencias de terceros (ffmpeg LGPL, yt-dlp Unlicense, Deno MIT) en «Acerca de» y en
  el instalador; avisos sobre derechos de autor y condiciones de cada sitio.
- Decidir el modelo (gratuito/código abierto, donaciones, versión de pago con soporte).

A partir de aquí los ciclos se alimentan del *backlog* que generen los informes de fallos, las
métricas y las incidencias de usuarios.

---

## 3. Estrategia de pruebas

| Nivel | Qué cubre | Herramienta | Cuándo |
|---|---|---|---|
| Unitarias | Parseo (HLS, DASH, yt-dlp), segmentación, nombres de archivo, ajustes | xUnit | Cada commit |
| Integración | Motor contra servidor HTTP local (rangos, sin rangos, tamaño desconocido, cortes), HLS/DASH, ffmpeg | xUnit + servidor de pruebas existente | Cada commit |
| Integración externa | yt-dlp/ffmpeg/Deno reales, batería de vídeos de referencia | Job nocturno | Cada noche |
| Interfaz | Flujos críticos de la app | FlaUI | Cada PR (desde el ciclo 4) |
| Manual | Matriz de abajo | Lista de comprobación | Congelación de cada ciclo |
| Beta | Uso real | Canal beta + informes opt-in | Días 10–13 |

**Matriz manual mínima** (se marca en cada congelación):

| | Win 10 | Win 11 | Chrome | Edge | Brave | Sin ffmpeg/yt-dlp | Sin red a mitad |
|---|---|---|---|---|---|---|---|
| Instalar / actualizar / desinstalar | ☐ | ☐ | — | — | — | — | ☐ |
| Captura desde el navegador | ☐ | ☐ | ☐ | ☐ | ☐ | — | — |
| Descarga HTTP multi-conexión + reanudar | ☐ | ☐ | ☐ | ☐ | — | — | ☐ |
| Vídeo HLS / DASH | ☐ | ☐ | ☐ | ☐ | — | ☐ | ☐ |
| YouTube (calidad, tamaño, pausa, reanudar) | ☐ | ☐ | ☐ | ☐ | — | ☐ | ☐ |
| Programador + apagado | ☐ | ☐ | — | — | — | — | — |

---

## 4. Indicadores para decidir

| Indicador | Objetivo |
|---|---|
| Descargas completadas / iniciadas (por tipo) | ≥ 98 % HTTP, ≥ 95 % yt-dlp |
| Sesiones sin fallo de la app | ≥ 99,5 % |
| Tiempo de «Iniciar» al primer byte (yt-dlp) | < 3 s (p90) |
| Tamaño exacto mostrado vs. final | ±1 % |
| Días desde que YouTube rompe algo hasta la corrección publicada | ≤ 2 |
| Bugs P0/P1 abiertos en una publicación estable | 0 |

Una publicación estable no sale si la beta empeora cualquiera de estos indicadores respecto a la
versión anterior.

---

## 5. Riesgos principales

| Riesgo | Mitigación |
|---|---|
| YouTube cambia y yt-dlp deja de funcionar | Actualización automática de yt-dlp (ya existe), batería nocturna que avisa antes que los usuarios |
| Políticas de las tiendas de extensiones | Variante sin YouTube para Chrome Web Store; Edge e instalación manual |
| SmartScreen / antivirus marcan la app | Firma de código desde el ciclo 2, reputación con el mismo certificado |
| Pérdida de descargas al actualizar | Migraciones de `downloads.json` con versión y test de actualización desde versiones anteriores |
| Uso para infringir derechos de autor | Avisos claros, sin evasión de DRM (ya se rechaza el contenido protegido) |
