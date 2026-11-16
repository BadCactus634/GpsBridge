# Componenti di terze parti

- **Leaflet** 1.9.4 (BSD-2-Clause), copiato in `src/GpsBridge.App/Assets/map/`. (c) Volodymyr Agafonnikov, Leaflet contributors.
- **Microsoft.Web.WebView2** (Microsoft Software License Terms) e **System.IO.Ports** (MIT), da NuGet.
- Tile cartografiche: (c) contributori di **OpenStreetMap** (ODbL), **OpenTopoMap** (CC-BY-SA), **Esri World Imagery**.
  L'app le scarica una a una mentre si usa la mappa e le tiene in cache; non fa download massivi.
  Rispetta le rispettive condizioni d'uso, in particolare la
  [tile usage policy di OpenStreetMap](https://operations.osmfoundation.org/policies/tiles/).
