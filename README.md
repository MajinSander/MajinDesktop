<p align="center"><img src="assets/icon.png" width="128" alt="MajinDesktop"></p>

<h1 align="center">MajinDesktop</h1>

<p align="center">Guarda tus pantallas y los iconos del escritorio en estados y cambia de uno a otro con un atajo de teclado.</p>

<p align="center"><img src="docs/settings.png" width="640" alt="Ventana de MajinDesktop"></p>

## Qué hace

- **9 estados del escritorio.** Cada estado guarda dos cosas:
  - **Pantallas:** la posición y la orientación (horizontal o vertical) de cada una, y por tanto cuál es la principal.
  - **Iconos del escritorio:** dónde está cada uno, apuntado respecto a su pantalla. Así, al cambiar de disposición, cada icono vuelve a su monitor y a su sitio, en lugar de quedar como Windows decida.

  Coloca todo como quieras y pulsa *Guardar actual*. Con los dos botones de cada fila eliges qué partes aplica ese estado; por ejemplo, solo los iconos.
- **Atajos globales.** `Ctrl+Alt+Shift+1…9` aplican cada estado, `Ctrl+Alt+Shift+0` pasa al siguiente y `Ctrl+Alt+Shift+Retroceso` deshace el último cambio (pulsándolo otra vez, lo rehace). Se cambian haciendo clic en la caja y pulsando la combinación. Si otra aplicación ya usa una combinación, la caja se pone en rojo.
- **Reconoce cada monitor.** Identifica cada pantalla por modelo y puerto, así que no se lía aunque Windows cambie los números DISPLAY1/2/3.
- **Aviso en pantalla.** Al cambiar de estado aparece un aviso pequeño con un dibujo de la disposición. No te quita el foco.
- **Bandeja del sistema.** Con clic izquierdo abres la configuración. Con clic derecho, un menú con tus estados (el activo lleva ✓) para guardar, cambiar de estado, iniciar con Windows o salir.
- **Se actualiza sola.** Si la tienes instalada, al arrancar y cada 6 horas mira si hay una release nueva en este repositorio. Si la hay, la instala en silencio y se vuelve a abrir. La versión portable solo avisa, y el menú de la bandeja tiene *Buscar actualizaciones*.
- **Muy ligero.** La app pesa unos 100 KB, ocupa unos 4 MB de RAM y no gasta CPU mientras espera un atajo.
- **Línea de comandos** para Stream Deck o tus propios scripts:

  ```
  MajinDesktop.exe --list          pantallas actuales y estados guardados
  MajinDesktop.exe --icons         iconos del escritorio y en qué pantalla está cada uno
  MajinDesktop.exe --save 3        guarda las pantallas y los iconos actuales en el estado 3
  MajinDesktop.exe --apply 3       aplica el estado 3
  MajinDesktop.exe --validate 3    comprueba el estado 3 sin aplicarlo (y cuántos iconos se moverían)
  ```

No cambia la resolución, la frecuencia ni la escala, tampoco enciende ni apaga pantallas, y **nunca toca el fondo de pantalla** (convive con Wallpaper Engine). Si un estado no incluye una pantalla que ahora está conectada, esa pantalla se queda donde está; si choca con otra, se mueve a la derecha. Los iconos que no estaban al guardar el estado se quedan donde estén. Si tienes activado *Organizar iconos automáticamente*, se desactiva al colocar los iconos, porque con esa opción Windows ignora las posiciones.

Lo que viene después está en el [plan de mejoras](ROADMAP.md): modo TV (encender y apagar pantallas, HDR, audio) y un organizador visual de accesos directos de juegos.

## Descarga

En [Releases](https://github.com/MajinSander/MajinDesktop/releases/latest) están:

| Archivo | Para qué |
|---|---|
| `MajinDesktop-Setup-x.y.z.exe` | **Instalador (recomendado).** Se instala para tu usuario, sin pedir permisos de administrador. Crea el acceso del menú Inicio y, si quieres, el inicio con Windows. Se desinstala desde *Aplicaciones instaladas*. |
| `MajinDesktop.exe` | Versión portable. Se abre sin instalar nada. |

Solo necesita .NET Framework 4.8, que ya viene con Windows 10 (desde la versión 1903) y con Windows 11.

## Datos

Los estados se guardan en `%APPDATA%\MajinDesktop\config.json`, un archivo de texto que puedes abrir y editar. Si vienes de la 0.1, el `config.ini` se convierte solo. Si el archivo se estropea, se aparta como `config.broken-….json` y la app empieza de cero en lugar de cerrarse. Los errores se apuntan en `log.txt`, en la misma carpeta. Si defines la variable `MAJINDESKTOP_DATA`, la app usa esa carpeta en su lugar, lo que sirve para hacer pruebas.

## Compilar

```
pwsh -File build.ps1 -Version 0.1.0
```

Antes de compilar se ejecutan las pruebas de `tests/` (colocación de pantallas e iconos, atajos y configuración); si alguna falla, no se genera nada. Hace falta el SDK de .NET 8 o superior. Se usa su compilador Roslyn contra los ensamblados de .NET Framework 4.8 que trae Windows, así que el código puede usar C# moderno y el resultado no depende de ningún runtime extra. Los ejecutables quedan en `dist\`.

Al publicar una release en GitHub, el workflow `release.yml` compila la app y adjunta los dos ejecutables.

---

**English:** MajinDesktop saves your monitor layout (position and orientation) and your desktop icon positions, stored per monitor, in 9 states, and switches between them with global hotkeys. It's a tray app of about 100 KB that uses about 4 MB of RAM. Download the installer or the portable exe from [Releases](https://github.com/MajinSander/MajinDesktop/releases/latest). The [roadmap](ROADMAP.md) explains how it will grow into a full desktop state manager.
