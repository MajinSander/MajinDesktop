# Plan de mejoras

El objetivo es que MajinDesktop pase de "slots de pantallas" a un **gestor de estados del escritorio**. Un estado es una foto de cómo quieres tu PC para algo concreto: escritorio de trabajo, jugar en la TV o streaming. Además tendrá un **organizador visual** del escritorio pensado sobre todo para los accesos directos de juegos.

Hay tres reglas que se mantienen en todas las fases:

1. **Ligero.** La parte residente (bandeja + atajos) sigue en unos pocos MB y 0 % de CPU. Las ventanas pesadas (organizador, biblioteca) solo existen mientras están abiertas y se liberan al cerrarlas.
2. **Nada irreversible.** Todo lo que cambia el escritorio se puede deshacer, y todo lo que se borra va a la papelera de Windows.
3. **Los fondos no se tocan.** De eso se encarga Wallpaper Engine: MajinDesktop no cambia el fondo ni pinta nada en el escritorio.

---

## Qué es un "estado"

Un estado tiene un nombre, un atajo y un color, y guarda solo las partes que tú marques:

| Parte | Qué guarda | Fase |
|---|---|---|
| Pantallas | posición, orientación, principal *(ya está)*; encendida o apagada, resolución, Hz, HDR | 0.1 / 0.4 |
| Iconos | dónde está cada icono del escritorio, **relativo a cada pantalla** | 0.3 |
| Audio | salida de sonido predeterminada (por ejemplo, la TV por HDMI) | 0.4 |
| Escritorio | tamaño de los iconos, mostrarlos u ocultarlos, ocultar la barra de tareas | 0.3 / 0.4 |
| Acciones | abrir o cerrar programas, Steam Big Picture, un comando | 0.7 |

Ejemplo, **Modo TV**: solo la TV encendida, HDR activado, audio por la TV, barra oculta y Steam Big Picture abierto. Con otro atajo vuelves a **Escritorio** y todo, iconos incluidos, queda como estaba.

---

## v0.2 · Cimientos ✅

- **Deshacer el último cambio** con `Ctrl+Alt+Shift+Retroceso`. Antes de aplicar un slot se guarda cómo estaba todo; pulsarlo otra vez rehace el cambio.
- **Configuración en JSON** (`config.json`), preparada para los estados con varias partes. El `config.ini` de la 0.1 se migra solo. Si el archivo se estropea, se aparta y la app arranca igual.
- **Registro de errores** en `%APPDATA%\MajinDesktop\log.txt`.
- **Pruebas automáticas** de la lógica (colocación de pantallas, atajos, configuración y migración). Se ejecutan en cada compilación y en cada push.
- La prueba de cambio de pantallas contra Windows ya está hecha (acepta girar pantallas y cambiar la principal); queda el uso diario.

## v0.3 · Iconos dentro de los estados ✅

Esta fase arregla un problema clásico: al cambiar de pantallas, Windows desordena los iconos del escritorio.

- **Guardar y restaurar la posición de los iconos.** Se hace con la vía documentada de Windows: `IShellWindows` → vista del escritorio → `IFolderView2` (`GetItemPosition` / `SelectAndPositionItems`). No toca el registro, no reinicia Explorer y no toca ninguna ventana del fondo (Wallpaper Engine sigue a lo suyo).
- **Posiciones relativas a cada pantalla.** Cada icono se guarda como "pantalla + distancia a su esquina", no en coordenadas absolutas. Así, al cambiar de pantalla principal o de orden, cada icono sigue en su monitor. Si la pantalla ahora es más pequeña, el icono se queda dentro del borde.
- **Restauración tras cada cambio de pantallas.** Se espera a que Explorer termine de reacomodarse y se recolocan los iconos en dos pasadas.
- **Cada estado elige qué aplica** (pantallas, iconos o las dos cosas). *Deshacer* también devuelve los iconos.
- Si *Organizar automáticamente* está activado, se desactiva (con esa opción Windows ignora las posiciones) y se avisa.
- Comprobado en tu escritorio con 89 iconos: guardar y reaplicar mueve 0 iconos, y un icono movido a propósito vuelve exactamente a su sitio.

Pendiente para más adelante: tamaño de iconos y mostrar u ocultar los iconos por estado.

## v0.4 · Pantallas avanzadas, audio y modo TV

- **Encender y apagar pantallas** (por ejemplo, la TV) con `QueryDisplayConfig(QDC_ALL_PATHS)`. Windows cambia el identificador de la gráfica (LUID) en cada arranque, así que se corrige al aplicar.
- **Resolución y Hz** opcionales en cada estado.
- **HDR por pantalla** con `DisplayConfigSetDeviceInfo` (estado de color avanzado).
- **Salida de audio predeterminada.** Usa la misma interfaz interna de Windows que emplean programas como SoundSwitch o EarTrumpet. Queda aislada en un módulo por si alguna versión de Windows la cambia.
- **Red de seguridad.** Si un estado apaga pantallas o cambia la resolución, sale *"¿Mantener esta configuración? Se revierte en 15 s"* en la pantalla nueva. Además habrá un atajo de emergencia que vuelve al último estado marcado como seguro.
- **Barra de tareas:** ocultarla automáticamente por estado.

*Hecho cuando:* con un atajo pasas a jugar en la TV con HDR y sonido por HDMI, y con otro vuelves al escritorio.

## v0.5 · Organizador visual del escritorio

Es una ventana nueva con una **representación de tu escritorio**: tus pantallas a escala, la barra de tareas y cada icono en su sitio.

- **Seleccionar** con clic, `Ctrl`+clic o un rectángulo. **Arrastrar** iconos, sueltos o en grupo, incluso a otra pantalla.
- **Herramientas de orden:** alinear, repartir, ajustar a la cuadrícula real de Windows, compactar huecos y ordenar por nombre, tipo, plataforma o fecha.
- **Zonas** al estilo Fences, pero sin ventanas extra encima del escritorio. Por ejemplo "Juegos Steam" en la pantalla derecha o "Trabajo" en la izquierda. Son rectángulos con nombre que dibujas en el organizador, con reglas que deciden qué va en cada una (plataforma, tipo, carpeta, nombre). El botón *Ordenar* coloca cada icono en su zona.
- **Iconos nuevos:** cuando aparece un acceso directo nuevo en el escritorio (por ejemplo, al instalar un juego), se coloca solo en su zona.
- **Vista previa y aplicar.** Los cambios se ven en el organizador y se aplican al escritorio de una vez, con deshacer.
- Las zonas solo existen en el organizador: no se dibuja nada en el escritorio real, para no interferir con Wallpaper Engine.

*Hecho cuando:* desordenas el escritorio, pulsas *Ordenar* y cada juego vuelve a su zona.

## v0.6 · Accesos directos de juegos

- **Inventario** de todo lo que hay en el escritorio (el tuyo y el público), clasificado: juego de Steam (`steam://rungameid/…`), Epic, GOG, EA, Ubisoft, Xbox, programa, carpeta, archivo, **roto** (el destino ya no existe) y **duplicado**.
- **Biblioteca de juegos instalados.**
  - Lee Steam (`libraryfolders.vdf` y `appmanifest_*.acf`), Epic (manifiestos `.item`), GOG, EA y Ubisoft (registro).
  - Marca qué juegos aún no tienen acceso directo y los **crea en lote**, con su icono, directamente en la zona que elijas.
- **Iconos.**
  - **Arreglar los iconos en blanco de Steam.** El `.url` ya indica qué `.ico` falta, así que se descarga de la CDN de Steam.
  - **Cambiar el icono** desde el `.exe` del juego, desde la caché de Steam o desde cualquier PNG o JPG, que se convierte a `.ico` de varios tamaños y se guarda en la carpeta de la app.
  - Al terminar, se avisa a Explorer para que refresque la caché de iconos sin reiniciar.
- **Operaciones en lote.**
  - Renombrar: quitar " - Acceso directo", ™ y ®, poner mayúsculas.
  - Mover a una zona o a una carpeta.
  - Cambiar el icono de varios a la vez.
  - Mandar los rotos a la papelera.
- **Diario de operaciones.** Cada cambio en un lote queda apuntado, así que *Deshacer* revierte el lote entero.

*Hecho cuando:* instalas tres juegos y en dos clics tienen acceso directo, icono correcto y sitio en su zona.

## v0.7 · Disparadores automáticos y acciones

- **Al conectar o desconectar una pantalla:** por ejemplo, al encender la TV se aplica *Modo TV*, y al apagarla se vuelve a *Escritorio*.
- **Al abrir un juego o programa:** se aplica un estado y al cerrarlo se vuelve al anterior. Se detecta con eventos de Windows (`SetWinEventHook`), sin consultar la lista de procesos en bucle.
- **Al iniciar Windows:** aplicar un estado concreto.
- **Acciones** dentro de un estado: abrir o cerrar programas, `steam://open/bigpicture`, un comando, esperar unos segundos.

## v1.0 · Pulido

- **Paleta rápida** (`Ctrl+Alt+Shift+Espacio`): un buscador flotante para saltar a cualquier estado o acción.
- **Exportar e importar** estados, y una copia de seguridad del escritorio (accesos directos + posiciones) en un `.zip`.
- **Interfaz en inglés.**
- Firma del ejecutable, para que SmartScreen y los antivirus no lo traten como desconocido.

---

## Decisiones técnicas

- **Se sigue con C# + WinForms sobre .NET Framework 4.8**, sin dependencias. Electron (como MajinGallery) tendría más de 100 MB residentes y chocaría con "ligero". WPF no aporta lo suficiente para lo que cuesta. El organizador será un lienzo dibujado a mano, igual que las miniaturas de pantallas que ya existen.
- **Todo con APIs de Windows por COM:** `IFolderView`, `IShellLink` y la interfaz de audio. Se declaran a mano en el código, sin librerías de terceros.
- **Instalador propio**, construido con el mismo compilador: se instala por usuario, sin permisos de administrador, y se desinstala desde *Aplicaciones instaladas*. La actualización automática (ya incluida desde la 0.1.0) descarga el instalador de la última release y lo lanza con `/S`.

## Riesgos conocidos

- **Posición de iconos con varias pantallas.** El origen de coordenadas del escritorio cambia con la disposición. Por eso se guardan relativas a cada pantalla.
- **La interfaz de audio no está documentada.** Lleva años estable, pero queda aislada en un módulo por si Windows la cambia.
- **El HDR por API** ha cambiado entre versiones de Windows 11: habrá que probarlo en la tuya.
- **Apagar pantallas por error** puede dejarte sin imagen. Para eso están la cuenta atrás de 15 s y el atajo de emergencia.
- **Antivirus.** Un programa sin firma que toca accesos directos y el registro puede dar falsos positivos.

## Para más adelante (sin fecha)

- Iconos desde SteamGridDB (necesita clave de API).
- Elegir estado con el mando en la TV, al estilo del modo TV de MajinGallery.
- Ventanas nítidas en pantallas con distinta escala (Per-Monitor DPI v2). Ahora mismo tus tres pantallas están al 100 %, así que no hace falta.
- Escala de Windows (100 %, 125 %…) por estado. Solo se puede con una API no documentada, así que tiene más riesgo.

---

**English summary:** MajinDesktop will grow from monitor-layout slots into a desktop state manager. A state can hold monitors (including on/off, resolution, refresh rate and HDR), desktop icon positions stored per monitor, default audio output and actions. Planned releases:

- **v0.2** foundations: undo, JSON config, error log, automated tests (done).
- **v0.3** desktop icons inside states (done).
- **v0.4** advanced monitors, audio and a TV mode with a safety revert.
- **v0.5** a visual desktop organizer with Fences-style zones.
- **v0.6** game shortcuts: library scan, batch creation, icon fixing and batch operations with undo.
- **v0.7** automatic triggers.
- **v1.0** polish.
