# Edge 510 · Asistente OPNsense

Aplicación Windows en español, desarrollada por **George Valdez**, para guiar la conversión de un VeloCloud Edge 510 a OPNsense. Interfaz paso a paso con inventario del PC, consola serial, comprobación de imagen, grabación segura del USB y instalación guiada en la memoria interna.

## Flujo del asistente

1. **Consola y USB:** conecta primero el puerto COM del Edge. Después selecciona la memoria en una ventana de Windows por modelo, capacidad y serie. Sin ambas conexiones no se puede continuar.
2. **Descargar OPNsense:** descarga la imagen Serial oficial, verifica su firma y prepara la consola UART1 del Edge.
3. **Crear USB:** revisa la unidad, confirma el borrado y espera la grabación con lectura de verificación.
4. **Instalar en el Edge:** conecta y respalda el equipo, autoriza el destino y comprueba el arranque interno sin USB durante más de diez minutos.

La consola y su ayuda están en el primer paso. La instalación reúne los botones Respaldar, Instalar y Verificar en una sola fila. El informe permanece visible en un área de altura limitada. Abrir el puerto confirma su disponibilidad; la salida serial permite comprobar que el Edge responde.

El programa usa la contraseña de root en memoria durante la sesión SSH con el Edge. Mantiene los registros y respaldos localmente en `%LOCALAPPDATA%\GeorgeValdez\Edge510OPNsense`. La grabación del USB requiere elevación y borra ese USB. La preparación guarda los dos bytes originales del watchdog del PIC y los desactiva con verificación de lectura. La instalación interna requiere un respaldo completo verificado y autorización sobre el destino identificado. El BIOS existente permite arrancar esta conversión.

## Descargar

Descarga el [ejecutable Windows x64 v0.3.1](https://github.com/georgevaldez/edge510-opnsense-installer/releases/download/v0.3.1/Edge510-OPNsense-Installer.exe) o abre la [página de versiones](https://github.com/georgevaldez/edge510-opnsense-installer/releases).

La compilación portable para Windows x64 está en `release/Edge510-OPNsense-Installer.exe`. Es un único ejecutable con .NET 10 incluido; no requiere instalar el runtime. En Windows, ejecuta como usuario normal para diagnóstico y preparación. La aplicación avisa cuando necesite permisos elevados para escribir el USB. El ejecutable no está firmado con un certificado comercial, por lo que Windows puede mostrar una advertencia de SmartScreen.

## Compilar desde el código fuente

Requisitos: Windows 10/11, .NET 10 SDK y acceso a NuGet para restaurar dependencias.

```powershell
dotnet publish .\GeorgeValdez.Edge510Installer\GeorgeValdez.Edge510Installer.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -o .\release
```

Las dependencias directas están fijadas en el proyecto y la clave pública de OPNsense 26.7 está fijada en código. Si el mirror publica otra versión estable, la app **se detiene** y pide actualizar la clave confiable y recompilar; nunca acepta en silencio una clave obtenida del mismo mirror que entrega la imagen.

## Desarrollo

- Proyecto WPF: `GeorgeValdez.Edge510Installer/`
- Decisiones y riesgos: `docs/DECISIONES-Y-RIESGOS.md`
- Operación: `docs/OPERACION.md`
- Ajustes del appliance y luz frontal: `appliance/`

La interfaz no usa marcas de terceros como autoría. © 2026 George Valdez. OPNsense y VeloCloud son marcas de sus respectivos titulares; este proyecto es independiente y no está afiliado a ellos.
