# Changelog

## 1.1.1 - 2026-11-16
- **Costellazioni**: il ricevitore (u-blox M10) puo' usarne al massimo 3 insieme; con 4 accese tornava al solo GPS. L'app ora legge il limite (UBX-MON-GNSS), attiva GPS + Galileo + GLONASS (BeiDou solo se la scegli al loro posto) e lo spiega in Impostazioni e Diagnostica.
- Mappa: se si apriva dopo il primo fix, il puntino non compariva e la vista restava sull'Europa.
- L'installer riapre l'app con i permessi dell'utente, non da amministratore.

## 1.1.0 - 2026-11-16
Prima versione rifinita per il rilascio.
- Icona dell'app, schermata «Informazioni» con versione, data di compilazione e crediti.
- Controllo aggiornamenti su GitHub Releases all'avvio (disattivabile): banner con «Scarica e installa» (verifica SHA-256, l'app si riapre da sola).
- Tema chiaro, scuro o automatico; punto GPS più visibile; scala della mappa leggibile.
- Una sola copia dell'app alla volta; errori imprevisti scritti in `%AppData%\GpsBridge\errori.log` invece di chiudere l'app.
- Installer in italiano/inglese con icona, mutex, pulizia di «Avvia con Windows» alla disinstallazione.

## 1.0.0 - 2026-11-16
Prima pubblicazione: collegamento automatico, configurazione di tutte le costellazioni, mappa con cache offline, punti colorati, navigazione,
tracce GPX (avvia/pausa/stop/carica), diagnostica, posizione live in Google Earth via KML.
