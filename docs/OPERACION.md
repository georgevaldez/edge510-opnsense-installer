# Guía de operación

## 1. Elegir USB

Abre `Edge510-OPNsense-Installer.exe` y pulsa **Seleccionar USB…**. En la ventana de Windows, elige la memoria por modelo, capacidad y letra; revisa también su serie. Si no aparece, conéctala y pulsa **Actualizar**. Los discos del sistema quedan excluidos. El número de disco puede cambiar: el asistente comprueba de nuevo la identidad antes de escribir.

## 2. Descargar OPNsense

Pulsa **Descargar OPNsense**. El asistente descarga la imagen Serial oficial, comprueba la firma RSA-SHA256 y prepara una copia para UART1. Si la versión o la clave publicada difiere de la clave 26.7 fijada en el programa, se detiene. Consulta **Detalles de la imagen** si necesitas más información.

## 3. Crear USB

Revisa la unidad y pulsa **Grabar y verificar USB**. Confirma el borrado escribiendo la frase indicada. La grabación requiere ejecutar la aplicación como administrador. Si falla el bloqueo del dispositivo o la lectura de verificación, se detiene. Espera el mensaje de verificación antes de retirar la memoria.

## 4. Instalar en el Edge

Arranca con el USB Serial y asigna una dirección de administración accesible desde el PC. En **Instalar en el Edge**, introduce esa dirección y la contraseña de root. La contraseña del live nuevo es `opnsense`.

La opción de red de administración usa la puerta de enlace y los DNS del adaptador del PC que comparte la subred del Edge, y conserva el DHCP existente de esa LAN desactivando el servidor DHCP de la LAN del Edge. Revisa esa opción antes de preparar la instalación.

Pulsa **1. Conectar y respaldar**. El asistente comprueba el modelo EDGE510, el firmware 01.00.00.05, el arranque desde el medio de instalación y la memoria de fábrica Generic Ultra HS-COMBO de 7,125 GiB. Guarda la configuración original del PIC y desactiva su watchdog en EEPROM y en el contador activo. Después copia el disco completo al PC y compara su SHA-256 con una segunda lectura desde el Edge. Esta fase puede tardar por la velocidad de la memoria interna.

Revisa el modelo, la serie y la ruta del respaldo. Marca la autorización de borrado y pulsa **2. Instalar OPNsense**. El programa ejecuta el instalador oficial UFS, selecciona el destino identificado e instala la consola UART1 y el indicador de estado. Conserva la configuración de administración del entorno live. El respaldo y los registros quedan en la carpeta local de esa sesión.

Cuando termine el apagado, retira el USB del Edge y enciéndelo. Pulsa **3. Comprobar sin USB**. Se comprueban el dispositivo raíz, la serie del disco, las cuatro interfaces Intel, la web por HTTPS y el watchdog desactivado durante más de diez minutos con el mismo arranque.

El perfil automatizado cubre esta revisión y este modelo de memoria de fábrica. Una discrepancia detiene el proceso y deja el motivo en pantalla. Una instalación parcial necesita revisión del registro antes de reintentar el borrado.

## Consola y diagnóstico opcionales

En el paso 4, despliega **Consola y diagnóstico** para conectar el puerto COM a 115200 8N1. La app escucha hasta que pulses **Capturar diagnóstico** y confirmes el envío del inventario de solo lectura. Los registros quedan en `%LOCALAPPDATA%\GeorgeValdez\Edge510OPNsense`.

La ayuda de acceso está en **Ayuda para iniciar sesión**: el Linux de fábrica usa `root` y `VeloHelloXXX`, donde `XXX` corresponde a los últimos tres caracteres de la serie inferior, salvo que se haya cambiado la contraseña. El medio live de OPNsense usa `root` y `opnsense`.

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
