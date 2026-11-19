# Decisiones de instalación

## Perfil compatible

La instalación automatizada verifica EDGE510 revisión 1, BIOS VELOCLOUD-EDGE-01.00.00.05 y memoria interna Generic Ultra HS-COMBO de 7,125 GiB. La imagen es OPNsense 26.7 Serial amd64 con firma oficial comprobada. El asistente prepara una copia con UART1 a 0x2F8 y conserva el original.

## Watchdog del PIC

El reinicio de prueba devolvió el código 0x05 del PIC. La preparación guarda los bytes originales de EEPROM 0 y 1, escribe cero, espera la confirmación del controlador y verifica ambas posiciones y el contador activo. Los valores originales permiten preparar una restauración identificando primero la misma unidad.

El protocolo está documentado en el [código del PIC del Edge 510](https://github.com/junk-labs/velocloud-openwrt/blob/a58c72eadae384088e5b7bab31bd041abb5528d7/firmware/edge510-pic/pic-doc). El servicio de luces instalado consulta ese estado y controla los colores.

## Disco interno

Se usa el instalador oficial UFS y su distribución GPT/EFI. El programa comprueba el modelo, la serie y la capacidad antes de confirmar el destino. La memoria de este perfil cumple el mínimo de 4 GiB del instalador UFS incluido en la imagen. La operación elimina las particiones de fábrica después de guardar y verificar el respaldo completo. La selección de swap sigue la decisión del instalador para esta capacidad.

Los respaldos del disco y de la configuración del PIC se conservan en la carpeta local de la sesión, junto con sus registros. El respaldo del disco contiene datos privados. Debe mantenerse fuera del repositorio público.

## Confirmación final

La instalación se considera verificada cuando arranca con el USB retirado, la raíz pertenece a la memoria interna identificada, aparecen las cuatro interfaces Intel, el watchdog sigue desactivado y la web permanece disponible durante más de diez minutos con el mismo arranque. La luz empieza a mostrar el estado del sistema cuando se ejecuta el servicio de OPNsense.
