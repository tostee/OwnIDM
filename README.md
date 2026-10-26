# OwnIDM

Gestor de descargas para Windows inspirado en Internet Download Manager, escrito
desde cero en C# / .NET 8 (WPF). Software libre con licencia [MIT](LICENSE).

**Descarga:** el instalador de la última versión está en
[Releases](https://github.com/tostee/OwnIDM/releases/latest) (Windows 10/11, 64 bits; se actualiza solo).
Consulta la [política de firma de código](#code-signing-policy) y la [privacidad](#privacidad).

## Funciones

- **Descarga multi-conexión con segmentación dinámica** (hasta 32 conexiones):
  cuando una conexión termina su tramo, parte por la mitad el tramo con más bytes
  pendientes, así todas trabajan hasta el final.
- **Pausar / reanudar**, también tras cerrar la app (los segmentos se guardan en JSON).
- Reintentos con espera creciente, tiempo de espera configurable, redirecciones,
  cookies, referer y user-agent del navegador.
- **Categorías** por extensión (Comprimidos, Documentos, Música, Programas, Vídeo)
  con carpeta propia.
- **Cola principal** con N descargas simultáneas, "Descargar más tarde".
- **Limitador de velocidad** por descarga.
- **Descarga por lotes** (`img*.jpg` → img001…img100) e importar/exportar listas de URL.
- **Monitor del portapapeles**, arrastrar y soltar enlaces, icono en la bandeja,
  inicio con Windows.
- **Integración con Chrome / Edge / Brave / Vivaldi**: extensión MV3 + host de
  *native messaging*. Las descargas del navegador pasan a OwnIDM (solo se cancelan en el
  navegador si las aceptas); clic derecho en un enlace → «Descargar con OwnIDM».
- **Programador**: inicia la cola a una hora (ciertos días de la semana o una sola vez),
  la detiene a otra hora y, al terminar, puede cerrar OwnIDM, suspender o apagar el equipo
  tras una cuenta atrás cancelable. Orden de la cola con Subir/Bajar.
- **Grabber de sitios**: explora un sitio desde una dirección inicial (niveles de enlaces,
  otros sitios, sin subir de carpeta, robots.txt, límite de páginas), filtra por tipo
  (`zip pdf`, `*.iso`, `img??.jpg`, `*`), oculta duplicados y añade los marcados a la cola,
  opcionalmente manteniendo las subcarpetas del sitio.
- **Captura de vídeo por sitio**: la extensión detecta los vídeos que se reproducen
  (archivos MP4/WebM…, streams HLS `.m3u8` y DASH `.mpd`) y muestra un panel «Descargar vídeo»
  en la página y en su icono. Se activa/desactiva por sitio y con tamaño mínimo. OwnIDM descarga
  los streams por segmentos (reanudables) y deja elegir la calidad. Los HLS en MPEG-TS se
  convierten a `.mp4` sin recodificar si hay **ffmpeg**; los DASH (MP4 fragmentado, plantillas
  `SegmentTemplate`/`SegmentTimeline`, `SegmentList` o `SegmentBase` con índice `sidx`) ya
  salen en `.mp4` (`.webm` si son WebM). Cuando el sitio sirve el audio por separado, ffmpeg
  es obligatorio para unirlo. Un enlace `.m3u8`/`.mpd` pegado a mano también se trata como stream.
  Los vídeos con DRM o cifrados se marcan como protegidos y no se descargan.
- **YouTube y otros sitios de vídeo con [yt-dlp](https://github.com/yt-dlp/yt-dlp)** (opcional,
  como ffmpeg): en las páginas de vídeo de YouTube el panel ofrece «YouTube · con yt-dlp», y en
  cualquier otra página el menú contextual «Descargar el vídeo de esta página con OwnIDM (yt-dlp)»
  sirve para los [sitios que soporta yt-dlp](https://github.com/yt-dlp/yt-dlp/blob/master/supportedsites.md).
  OwnIDM muestra las calidades (resolución, solo audio) con el tamaño de los archivos que se van a
  descargar: exacto cuando el sitio lo publica y marcado con «≈» si es una estimación. La página leída
  al consultar el vídeo se reutiliza al iniciar la descarga (no se vuelve a leer), y el progreso, la
  cola, el limitador y pausar/reanudar funcionan como en el resto de descargas. Se usan las cookies del
  navegador del sitio, así que funcionan los vídeos que puedes ver con tu sesión. Respeta las
  condiciones de cada sitio y los derechos de autor.
- **Componentes que OwnIDM instala solo**: yt-dlp, ffmpeg (versión LGPL de BtbN) y Deno (lo
  necesita yt-dlp para YouTube) no van en el instalador. La primera vez que hacen falta, OwnIDM
  ofrece descargarlos (unos 140 MB) de sus páginas oficiales en GitHub, con su propio motor de
  descarga, y comprueba cada archivo con la suma SHA-256 que publica el proyecto. Se instalan en
  `%LOCALAPPDATA%\OwnIDM\tools`, sin permisos de administrador. yt-dlp se actualiza solo: lo comprueba
  una vez al día al arrancar y, si una descarga falla, busca una versión nueva (como mucho una vez
  por hora) y reintenta. También desde Opciones → Vídeo → «Instalar o actualizar componentes».
  Una ruta indicada a mano en Opciones tiene prioridad; si no, se usa la copia de OwnIDM y, si no
  hay, la del PATH.

- **Torrents** (archivos `.torrent` y enlaces `magnet:`) con [MonoTorrent](https://github.com/alanmcgovern/monotorrent)
  (C# puro, MIT): DHT, intercambio de pares, descubrimiento local, UPnP/NAT-PMP y cifrado. Antes de empezar se
  muestran los archivos del torrent para elegir cuáles bajar (con un magnet, en cuanto los pares envían la lista;
  también se puede iniciar sin esperar). Los archivos se escriben directamente en la carpeta de destino; pausar
  conserva lo verificado y reanudar no vuelve a comprobarlo. Cola, programador, limitador y categorías (según el
  archivo más grande) funcionan como en el resto. Se añaden desde «Añadir descarga» (pegando el magnet), Tareas →
  «Abrir archivo torrent…», arrastrándolos a la ventana, el portapapeles, los enlaces `.torrent` que capture la
  extensión y su menú sobre un enlace magnet. En Opciones → Torrent: puerto, DHT, UPnP, conexiones, límite de
  subida y «Abrirlos con OwnIDM» (asociación por usuario de `.torrent` y `magnet:`; si Windows tiene elegido
  otro programa, abre «Aplicaciones predeterminadas»). OwnIDM comparte mientras descarga y deja de compartir al
  terminar. Descarga solo contenido que tengas derecho a compartir.

## Instalador

```powershell
winget install JRSoftware.InnoSetup      # una vez
powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1
```

Genera `dist\OwnIDM-Setup-<versión>.exe` (≈ 3 MB) y `dist\latest.json`: instala para el usuario actual
sin permisos de administrador, descarga .NET 8 Desktop Runtime de Microsoft si falta y, al desinstalar,
quita el registro del navegador, el inicio con Windows y los componentes descargados (conserva la lista
de descargas y los ajustes). Si falta `OwnIDM.exe` tras compilar (lo ha borrado el antivirus), el script
se detiene en vez de generar un instalador sin el programa.

## Actualizaciones

OwnIDM busca versiones nuevas al iniciar (una vez al día; se desactiva en Opciones → General) y desde
Ayuda → «Buscar actualizaciones…». Lee `latest.json`, descarga el instalador con su propio motor, comprueba
su SHA-256 y, si OwnIDM está firmado, que el instalador lo esté con el mismo certificado. Después se
cierra, el instalador sustituye los archivos sin preguntar (`/SILENT`) y vuelve a abrir OwnIDM.

`UpdateFeed` en `Directory.Build.props` apunta al `latest.json` del último release de este repositorio.

### Publicar una versión

Las versiones se compilan, firman y publican en GitHub Actions
([`.github/workflows/release.yml`](.github/workflows/release.yml)), nunca desde un PC:

1. Sube `Version` en `Directory.Build.props` (p. ej. `1.1.0`) y haz commit.
2. `powershell -ExecutionPolicy Bypass -File installer/publish-release.ps1 -Notes "Qué cambia"`: crea la
   etiqueta `v1.1.0` y la sube.
3. El workflow pasa los tests, compila, pide a SignPath que firme los binarios y luego el instalador
   (cada firma se aprueba a mano en SignPath) y crea el release con el instalador y `latest.json`.

Sin el secreto `SIGNPATH_API_TOKEN` en el repositorio, el workflow publica sin firmar. `build-installer.ps1`
sigue sirviendo para generar un instalador local de pruebas (y firma con un certificado propio si recibe
`-SignArgs`).

## Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by
[SignPath Foundation](https://signpath.org).

- Autores y revisores (*committers and reviewers*): [tostee](https://github.com/tostee)
- Aprobadores (*approvers*): [tostee](https://github.com/tostee)

Solo se firman los binarios compilados por GitHub Actions a partir del código de este repositorio
(`OwnIDM.exe`, `OwnIDM.dll`, `OwnIDM.Core.dll`, `OwnIDM.NativeHost.*` y el instalador). Las bibliotecas de
terceros (MonoTorrent y sus dependencias) se distribuyen tal como las publican sus autores. Los componentes
que OwnIDM descarga a petición del usuario (yt-dlp, ffmpeg, Deno) no se firman ni se incluyen en el instalador.

## Privacidad

OwnIDM no recoge datos personales ni tiene telemetría: no transfiere información a otros sistemas salvo
cuando el usuario lo pide (sus descargas) y en las comprobaciones de actualizaciones, que solo descargan
archivos públicos de GitHub y se desactivan en Opciones → General. El detalle completo, que el instalador
muestra antes de instalar, está en [installer/PRIVACIDAD.txt](installer/PRIVACIDAD.txt). Política de
privacidad de GitHub: <https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement>.

## Antivirus

Un `.exe` nuevo, sin firmar y sin reputación que escribe en el registro, vigila el portapapeles y descarga
y ejecuta programas (yt-dlp, ffmpeg) parece malware a la heurística de comportamiento (Kaspersky lo
detecta como `PDM:Trojan.Win32.Generic`). Por eso las versiones publicadas se firman (ver arriba).

## Compilar y ejecutar

Requisitos: Windows 10/11 y .NET 8 SDK.

```powershell
dotnet build OwnIDM.sln -c Release
.\build\Release\OwnIDM.exe
```

Tests (servidor HTTP local con y sin soporte de rangos, pausa/reanudación, tamaño desconocido):

```powershell
dotnet test tests\OwnIDM.Core.Tests
```

## Integración con el navegador

1. Arranca OwnIDM una vez: registra el host `com.ownidm.host` en
   `HKCU\Software\<navegador>\NativeMessagingHosts` (sin permisos de administrador).
2. En `chrome://extensions` (o `edge://extensions`) activa el **Modo de desarrollador**,
   pulsa **Cargar descomprimida** y elige `build\<Configuración>\extension`
   (Opciones → Navegador → «Abrir carpeta de la extensión»).
3. Listo. El icono de la extensión activa/desactiva la captura.

La extensión tiene una clave fija en su `manifest.json`, por lo que su ID es siempre
`jnnkhifhlmmlofgelokpmjcgpflmgpld`, que es el único origen autorizado en el host.

## Estructura

| Carpeta | Contenido |
|---|---|
| `src/OwnIDM.Core` | Motor de descarga, torrents (`Torrents/`), gestor de cola, ajustes, persistencia, IPC por *named pipe* |
| `src/OwnIDM.App` | Interfaz WPF (`OwnIDM.exe`) |
| `src/OwnIDM.NativeHost` | Host de *native messaging* que reenvía al programa |
| `extension` | Extensión MV3 para navegadores Chromium |
| `tests/OwnIDM.Core.Tests` | Tests xUnit del motor |
| `installer` | Script de Inno Setup y `build-installer.ps1` |

Datos del usuario: `%APPDATA%\OwnIDM` (lista y ajustes), `%LOCALAPPDATA%\OwnIDM\Temp`
(archivos `.part` en curso), `%LOCALAPPDATA%\OwnIDM\tools` (yt-dlp, ffmpeg y Deno) y
`%LOCALAPPDATA%\OwnIDM\Torrent` (metadatos de los torrents, fast-resume y caché de la DHT).

## Pendiente respecto a IDM

FTP, proxy manual, autenticación HTTP por sitio, proyectos guardados y programación del
grabber, conversión de enlaces para navegación sin conexión, DASH en directo o con varios
periodos (p. ej. anuncios intercalados).
