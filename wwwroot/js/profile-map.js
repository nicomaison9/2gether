window.profileMap = {
    maps: new Map(),

    render(elementId, profiles, currentLocation, componentReference) {
        const element = document.getElementById(elementId);
        if (!element) return;

        if (!window.L) {
            element.innerHTML = '<div class="map-unavailable">La bibliothèque de carte n’a pas pu être chargée.</div>';
            return;
        }

        let map = this.maps.get(elementId);
        if (map && map.getContainer() !== element) {
            map.remove();
            this.maps.delete(elementId);
            map = null;
        }

        if (!map) {
            map = L.map(element, { scrollWheelZoom: true }).setView([45.764, 4.8357], 13);
            L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
                maxZoom: 19,
                attribution: '&copy; OpenStreetMap contributors'
            }).addTo(map);
            map.profileMarkers = L.featureGroup().addTo(map);
            this.maps.set(elementId, map);
        }

        map.invalidateSize();
        map.profileMarkers.clearLayers();
        const bounds = [];

        for (const profile of profiles) {
            const markerPosition = [profile.latitude, profile.longitude];
            const marker = L.circleMarker(markerPosition, {
                radius: 9,
                color: '#ffffff',
                weight: 3,
                fillColor: '#e56d56',
                fillOpacity: 1,
                className: 'profile-marker'
            });
            const popup = document.createElement('div');
            popup.className = 'map-popup';
            const name = document.createElement('strong');
            name.textContent = profile.name;
            const area = document.createElement('span');
            area.textContent = `${profile.area} · ${profile.distanceKm.toFixed(1)} km`;
            const openButton = document.createElement('button');
            openButton.type = 'button';
            openButton.textContent = 'Voir le profil';
            openButton.addEventListener('click', () => componentReference.invokeMethodAsync('OpenProfileFromMap', profile.id));
            popup.append(name, area, openButton);
            marker.bindPopup(popup);
            marker.addTo(map.profileMarkers);
            bounds.push(markerPosition);
        }

        if (currentLocation) {
            const userPosition = [currentLocation.latitude, currentLocation.longitude];
            L.marker(userPosition, {
                icon: L.divIcon({ className: 'user-location-marker', html: '<span></span>', iconSize: [18, 18], iconAnchor: [9, 9] }),
                title: 'Ta position GPS'
            }).bindPopup('Ta position GPS (visible uniquement sur ton appareil)').addTo(map.profileMarkers);
            bounds.push(userPosition);
        }

        if (bounds.length > 1) map.fitBounds(bounds, { padding: [34, 34], maxZoom: 15 });
        else if (bounds.length === 1) map.setView(bounds[0], 14);
        else map.setView([45.764, 4.8357], 13);
        requestAnimationFrame(() => map.invalidateSize());
    },

    dispose(elementId) {
        const map = this.maps.get(elementId);
        if (!map) return;
        map.remove();
        this.maps.delete(elementId);
    }
};
