import { Permissions, webMethod } from 'wix-web-module';
import { fetch } from 'wix-fetch';

export const handleNotAdminAuth = webMethod(Permissions.Anyone, async (accType, idToken) => {

    if (accType == 'logout') { // logout
        try {
            const httpResponse = await fetch(`https://unity-app-backend-g5bfaedhawekhyay.australiaeast-01.azurewebsites.net/api/auth/logout-web`, {
                method: 'POST',
                credentials: 'include'
            });
            if (!httpResponse.ok) {
                const httpResponseMsg = (await httpResponse.text()).slice(10, -2);
                throw new Error(httpResponse.status + ' ' + httpResponse.statusText + ' - ' + httpResponseMsg);
            };

            return await httpResponse.ok;
        }
        catch (err) {
            console.log('handleAuthLogout -', err);
            return null;
        };
    }
    else if (accType == 'refresh') { // refresh // this don't work becuz Wix Velo is annoying
        try {
            const httpResponse = await fetch(`https://unity-app-backend-g5bfaedhawekhyay.australiaeast-01.azurewebsites.net/api/auth/refresh-web-1.5`, {
                method: 'POST',
                credentials: 'include'
            });
            if (!httpResponse.ok) {
                const httpResponseMsg = (await httpResponse.text()).slice(10, -2);
                throw new Error(httpResponse.status + ' ' + httpResponse.statusText + ' - ' + httpResponseMsg);
            };

            return await httpResponse.ok;
        }
        catch (err) {
            console.log('handleAuthRefresh -', err);
            return null;
        };
    }
    else if (accType == 'facebook' && idToken != '') { // meta wants it as accessToken
        try {
            const httpResponse = await fetch(`https://unity-app-backend-g5bfaedhawekhyay.australiaeast-01.azurewebsites.net/api/auth/facebook-login-web-1.5`, {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json'
                },
                body: JSON.stringify({
                    'accessToken': idToken
                })
            });
            if (!httpResponse.ok) {
                const httpResponseMsg = (await httpResponse.text()).slice(10, -2);
                throw new Error(httpResponse.status + ' ' + httpResponse.statusText + ' - ' + httpResponseMsg);
            };

            return await httpResponse.json();
        }
        catch (err) {
            console.log('handleAuthFacebook -', err);
            return null;
        };
    }
    else if (accType == 'google' && idToken != '') { // google
        try {
            const httpResponse = await fetch(`https://unity-app-backend-g5bfaedhawekhyay.australiaeast-01.azurewebsites.net/api/auth/google-login-web-1.5`, {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json'
                },
                body: JSON.stringify({
                    'idToken': idToken
                })
            });
            if (!httpResponse.ok) {
                const httpResponseMsg = (await httpResponse.text()).slice(10, -2);
                throw new Error(httpResponse.status + ' ' + httpResponse.statusText + ' - ' + httpResponseMsg);
            };

            return await httpResponse.json();
        }
        catch (err) {
            console.log('handleAuthGoogle -', err);
            return null;
        };
    }
    else if (accType == 'apple' && idToken != '') { // apple wants name and email
        var fullName = ''; // default to emtpy string if user already exists
        var email = ''; // default to emtpy string if user already exists
        if ('user' in idToken) { // only the first Apple login will return name and email
            fullName = idToken.user.name.firstName + ' ' + idToken.user.name.lastName;
            email = idToken.user.email;
        };
        try {
            const httpResponse = await fetch(`https://unity-app-backend-g5bfaedhawekhyay.australiaeast-01.azurewebsites.net/api/auth/apple-login-web-1.5`, {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json'
                },
                body: JSON.stringify({
                    'idToken': idToken.authorization.id_token,
                    'FullName': fullName,
                    'Email': email
                })
            });
            if (!httpResponse.ok) {
                const httpResponseMsg = (await httpResponse.text()).slice(10, -2);
                throw new Error(httpResponse.status + ' ' + httpResponse.statusText + ' - ' + httpResponseMsg);
            };

            return await httpResponse.json();
        }
        catch (err) {
            console.log('handleAuthApple -', err);
            return null;
        };
    };

});

export const handleAdminAuth = webMethod(Permissions.Anyone, async (accType, idToken) => {

    if (accType == 'meta' && idToken != '') { // meta wants it as accessToken
        try {
            const httpResponse = await fetch(`https://unity-app-backend-g5bfaedhawekhyay.australiaeast-01.azurewebsites.net/api/admin-auth/meta-admin`, {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json'
                },
                body: JSON.stringify({
                    'accessToken': idToken
                })
            });
            if (!httpResponse.ok) {
                const httpResponseMsg = (await httpResponse.text()).slice(10, -2);
                throw new Error(httpResponse.status + ' ' + httpResponse.statusText + ' - ' + httpResponseMsg);
            };

            return await httpResponse.json();
        }
        catch (err) {
            console.log('handleAdminAuthMeta -', err);
            return null;
        };
    }
    else if (accType == 'google' && idToken != '') { // google
        try {
            const httpResponse = await fetch(`https://unity-app-backend-g5bfaedhawekhyay.australiaeast-01.azurewebsites.net/api/admin-auth/google-admin`, {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json'
                },
                body: JSON.stringify({
                    'idToken': idToken
                })
            });
            if (!httpResponse.ok) {
                const httpResponseMsg = (await httpResponse.text()).slice(10, -2);
                throw new Error(httpResponse.status + ' ' + httpResponse.statusText + ' - ' + httpResponseMsg);
            };

            return await httpResponse.json();
        }
        catch (err) {
            console.log('handleAdminAuthGoogle -', err);
            return null;
        };
    }
    else if (accType == 'apple' && idToken != '') { // apple
        try {
            const httpResponse = await fetch(`https://unity-app-backend-g5bfaedhawekhyay.australiaeast-01.azurewebsites.net/api/admin-auth/apple-admin`, {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json'
                },
                body: JSON.stringify({
                    'idToken': idToken
                })
            });
            if (!httpResponse.ok) {
                const httpResponseMsg = (await httpResponse.text()).slice(10, -2);
                throw new Error(httpResponse.status + ' ' + httpResponse.statusText + ' - ' + httpResponseMsg);
            };

            return await httpResponse.json();
        }
        catch (err) {
            console.log('handleAdminAuthApple -', err);
            return null;
        };
    };

});

export const handleDeleteAccount = webMethod(Permissions.Anyone, async (jwtToken) => {

    if (jwtToken != '') {
        try {
            const httpResponse = await fetch(`https://unity-app-backend-g5bfaedhawekhyay.australiaeast-01.azurewebsites.net/api/user/delete`, {
                method: 'DELETE',
                headers: {
                    'Authorization': `Bearer ${jwtToken}`
                }
            });
            if (!httpResponse.ok) {
                const httpResponseMsg = (await httpResponse.text()).slice(10, -2);
                throw new Error(httpResponse.status + ' ' + httpResponse.statusText + ' - ' + httpResponseMsg);
            };
        }
        catch (err) {
            console.error('handleDeleteAccount -', err);
            return null;
        };
    };

});
