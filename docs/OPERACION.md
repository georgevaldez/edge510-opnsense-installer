# Guía de operación

## 1. Auditoría previa

Abre `release/Edge510-OPNsense-Installer.exe`. Pulsa **Analizar PC y unidades USB** y elige la memoria por serie, modelo, capacidad y letras asociadas. El número de disco puede cambiar; por eso se vuelve a comprobar antes de grabar.

## 2. Recoger evidencia serial

En la pantalla **Diagnóstico serial**, conecta el puerto a 115200 8N1. La app solo escucha hasta que pulses captura. Inicia sesión tú mismo con credenciales autorizadas; después confirma el envío del comando fijo de inventario de solo lectura. No se guardan contraseñas. Los datos quedan en `%LOCALAPPDATA%\GeorgeValdez\Edge510OPNsense`.

## 3. Revisar compatibilidad

Adjunta un informe que contenga la revisión de placa, versión BIOS, CPU, memoria, discos, PCI IDs de NIC, respaldo del firmware y método de recuperación. Una casilla marcada no hace una validación técnica: sirve para documentar la revisión. Aún no instales; una imagen que inicia no prueba que los drivers NIC/almacenamiento funcionen ni que la consola serial permanezca accesible.

## 4. Obtener y validar imagen

Pulsa **Descargar y verificar imagen oficial**. La app consulta el README del mirror para detectar la versión estable, comprueba que la clave publicada coincide exactamente con la clave 26.7 fijada en el ejecutable, descarga el archivo y su firma, descomprime y verifica RSA-SHA256. Si la versión o la clave ha cambiado, se detiene y hay que actualizar/recompilar el programa desde una fuente confiable.

## 5. Grabar USB (destructivo solo para el USB)

Tras verificar la imagen, ve a **Crear USB**. Confirma que la unidad mostrada es la correcta y que no hay otros discos USB conectados. Acepta la advertencia y escribe la frase exacta. Windows pedirá permisos elevados para grabar. Si Windows no puede bloquear/desmontar el volumen o falla la lectura posterior, se detiene y reporta error. Solo se escribe el dispositivo USB identificado.

## 6. Instalar dentro del Edge

Arranca con el USB Serial y asigna una dirección de administración accesible desde el PC. En **Convertir el appliance**, introduce esa dirección y la contraseña de root. La contraseña del live nuevo es `opnsense`.

La opción de red de administración usa la puerta de enlace y los DNS del adaptador del PC que comparte la subred del Edge, y conserva el DHCP existente de esa LAN desactivando el servidor DHCP de la LAN del Edge. Revisa esa opción antes de preparar la instalación.

Pulsa **Preparar Edge y guardar respaldo**. El asistente comprueba el modelo EDGE510, el firmware 01.00.00.05, el arranque desde el medio de instalación y la memoria de fábrica Generic Ultra HS-COMBO de 7,125 GiB. Guarda la configuración original del PIC y desactiva su watchdog en EEPROM y en el contador activo. Después copia el disco completo al PC y compara su SHA-256 con una segunda lectura desde el Edge. Esta fase puede tardar por la velocidad de la memoria interna.

Revisa el modelo, la serie y la ruta del respaldo. Marca la autorización de borrado y pulsa **Instalar OPNsense dentro del Edge**. El programa ejecuta el instalador oficial UFS, selecciona el destino identificado e instala la consola UART1 y el indicador de estado. Conserva la configuración de administración del entorno live. El respaldo y los registros quedan en la carpeta local de esa sesión.

Cuando termine el apagado, retira el USB del Edge y enciéndelo. Pulsa **Verificar el arranque interno sin USB**. Se comprueban el dispositivo raíz, la serie del disco, las cuatro interfaces Intel, la web por HTTPS y el watchdog desactivado durante más de diez minutos con el mismo arranque.

El perfil automatizado cubre esta revisión y este modelo de memoria de fábrica. Una discrepancia detiene el proceso y deja el motivo en pantalla. Una instalación parcial necesita revisión del registro antes de reintentar el borrado.

## Luz frontal

| Luz | Significado con el servicio del PIC activo |
| --- | --- |
| Azul, violeta y ámbar con respiración | OPNsense está arrancando y preparando la web |
| Verde | La web responde por HTTPS y el enlace LAN está activo |
| Ámbar | La web responde y falta enlace físico en la LAN |
| Rojo | Falló la web después de estar disponible, el arranque superó el plazo de preparación o el PIC informa de un problema |

El servicio se instala en `/usr/local/etc/rc.syshook.d/early/04-edge510-pic`. Consulta el watchdog desactivado y controla la luz. Si se pierde la comunicación con el PIC, el LED puede conservar el último color. Durante la fase previa del BIOS, la luz depende del firmware.

## Recuperación y privacidad

El respaldo del disco interno contiene el sistema y los datos originales de la unidad. Consérvalo junto con la configuración original del PIC y el respaldo SPI en un lugar privado, fuera de GitHub. La restauración completa del disco borra el destino y requiere volver a identificarlo.

El protocolo utilizado corresponde al [PIC del Edge 510](https://github.com/junk-labs/velocloud-openwrt/blob/a58c72eadae384088e5b7bab31bd041abb5528d7/firmware/edge510-pic/pic-doc). Los dos primeros bytes de EEPROM definen el watchdog predeterminado. El asistente guarda los originales antes de escribir cero y verifica la lectura posterior. Esta conversión utiliza el BIOS existente.

## Conservación de ajustes y medición de estabilidad

Los cambios de configuración toman el bloqueo de OPNsense y recargan la versión actual antes de escribir. Al terminar la copia del sistema, se transfiere la configuración vigente al destino y se habilita la administración SSH para la verificación.

La comprobación mantiene la sesión SSH y vigila contadores del kernel que crecen desde el arranque. La sincronización NTP puede cambiar la fecha de arranque presentada por el reloj, por lo que esa fecha se conserva como dato informativo. La disponibilidad web y el watchdog se comprueban durante más de diez minutos.

El servicio del indicador puede recuperar el controlador iSMT hasta tres veces si detecta fallos de comunicación con el PIC después de iniciar la web. Este controlador tiene un dispositivo PCI separado del almacenamiento USB interno.
