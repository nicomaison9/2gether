(() => {
    const decodeBase64Url = value => {
        const normalized = value.replace(/-/g, '+').replace(/_/g, '/');
        return Uint8Array.from(atob(normalized + '='.repeat((4 - normalized.length % 4) % 4)), character => character.charCodeAt(0));
    };

    const encodeBase64Url = value => {
        const bytes = new Uint8Array(value);
        let binary = '';
        for (const byte of bytes) binary += String.fromCharCode(byte);
        return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/g, '');
    };

    const prepareOptions = (options, includeUser) => {
        const publicKey = { ...options, challenge: decodeBase64Url(options.challenge) };
        if (includeUser && publicKey.user) publicKey.user = { ...publicKey.user, id: decodeBase64Url(publicKey.user.id) };
        for (const name of ['allowCredentials', 'excludeCredentials']) {
            if (publicKey[name]) publicKey[name] = publicKey[name].map(credential => ({ ...credential, id: decodeBase64Url(credential.id) }));
        }
        return publicKey;
    };

    const serializeCredential = credential => {
        if (!credential) throw new Error('Aucune passkey sélectionnée.');
        const response = credential.response;
        const payload = {
            id: credential.id,
            rawId: encodeBase64Url(credential.rawId),
            type: credential.type,
            authenticatorAttachment: credential.authenticatorAttachment,
            clientExtensionResults: credential.getClientExtensionResults(),
            response: {
                clientDataJSON: encodeBase64Url(response.clientDataJSON)
            }
        };
        if (response.attestationObject) {
            payload.response.attestationObject = encodeBase64Url(response.attestationObject);
            payload.response.transports = response.getTransports ? response.getTransports() : [];
        } else {
            payload.response.authenticatorData = encodeBase64Url(response.authenticatorData);
            payload.response.signature = encodeBase64Url(response.signature);
            payload.response.userHandle = response.userHandle ? encodeBase64Url(response.userHandle) : null;
        }
        return payload;
    };

    window.passkeys = {
        isSupported: () => Boolean(window.isSecureContext && window.PublicKeyCredential && navigator.credentials?.create && navigator.credentials?.get),
        create: async options => {
            if (!window.passkeys.isSupported()) throw new Error('Les passkeys ne sont pas disponibles sur cette origine.');
            const credential = await navigator.credentials.create({ publicKey: prepareOptions(options, true) });
            return serializeCredential(credential);
        },
        get: async options => {
            if (!window.passkeys.isSupported()) throw new Error('Les passkeys ne sont pas disponibles sur cette origine.');
            const credential = await navigator.credentials.get({ publicKey: prepareOptions(options, false) });
            return serializeCredential(credential);
        }
    };
})();
