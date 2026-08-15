import { session } from 'wix-storage-frontend';
import {handleNotAdminAuth} from 'backend/auth-handler.web';
import wixWindowFrontend from 'wix-window-frontend';
import wixLocationFrontend from 'wix-location-frontend';
import { fetch } from 'wix-fetch';

async function handleRefreshAuth(){
 try {
            const httpResponse = await fetch(`https://unity-app-backend-g5bfaedhawekhyay.australiaeast-01.azurewebsites.net/api/auth/refresh-web-1.5`, {
                method: 'POST',
                credentials: 'include'
            });
            if (!httpResponse.ok) {
                const httpResponseMsg = (await httpResponse.text()).slice(10, -2);
                throw new Error(httpResponse.status + ' ' + httpResponse.statusText + ' - ' + httpResponseMsg);
            };
            const data = await httpResponse.json();
            if (data != null) {
            session.setItem('accType', 'refresh');
            // session.setItem('idToken', data.idToken);
            // session.setItem('name', data.name);
            // session.setItem('email', data.email);
            session.setItem('jwtToken', data.accessToken);
            wixLocationFrontend.to(`https://www.driprun.com.au/drip-run-app`);
    }
            // return await httpResponse.ok;
        }
        catch (err) {
            console.log('handleAuthRefresh -', err);
            // return null;
    };
}


$w.onReady(function () {

    // loginButtonHandler('refresh', '');
    // var refresh = await handleRefreshAuth();
    handleRefreshAuth();

    $w("#html1").onMessage((event) => {
        loginButtonHandler('facebook', event.data);
    });

    $w("#html2").onMessage((event) => {
        loginButtonHandler('google', event.data);
    });

    $w("#html3").onMessage((event) => {
        loginButtonHandler('apple', event.data);
    });

});


async function loginButtonHandler (accType, idToken) {
    if (accType == 'google' && idToken != '') { // google
        try {
            const httpResponse = await fetch(`https://unity-app-backend-g5bfaedhawekhyay.australiaeast-01.azurewebsites.net/api/auth/google-login-web-1.5`, {
                method: 'POST',
                credentials: 'include',
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
            var authResponse = await httpResponse.json();
            if (authResponse != null) {
                session.setItem('accType', accType);
                session.setItem('idToken', idToken);
                session.setItem('name', authResponse.name);
                session.setItem('email', authResponse.email);
                session.setItem('jwtToken', authResponse.jwtToken);
                wixLocationFrontend.to(`https://www.driprun.com.au/drip-run-app`);
            }
        }
        catch (err) {
            console.log('handleAuthGoogle -', err);
            return null;
        };
    }else if (accType == 'facebook' && idToken != '') { // meta wants it as accessToken
        try {
            const httpResponse = await fetch(`https://unity-app-backend-g5bfaedhawekhyay.australiaeast-01.azurewebsites.net/api/auth/facebook-login-web-1.5`, {
                method: 'POST',
                credentials: 'include',
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
            var authResponse = await httpResponse.json();
            if (authResponse != null) {
                session.setItem('accType', accType);
                session.setItem('idToken', idToken);
                session.setItem('name', authResponse.name);
                session.setItem('email', authResponse.email);
                session.setItem('jwtToken', authResponse.jwtToken);
                wixLocationFrontend.to(`https://www.driprun.com.au/drip-run-app`);
            }
        }
        catch (err) {
            console.log('handleAuthFacebook -', err);
            return null;
        };
    }else if (accType == 'apple' && idToken != '') { // apple wants name and email
        var fullName = ''; // default to emtpy string if user already exists
        var email = ''; // default to emtpy string if user already exists
        if ('user' in idToken) { // only the first Apple login will return name and email
            fullName = idToken.user.name.firstName + ' ' + idToken.user.name.lastName;
            email = idToken.user.email;
        };
        try {
            const httpResponse = await fetch(`https://unity-app-backend-g5bfaedhawekhyay.australiaeast-01.azurewebsites.net/api/auth/apple-login-web-1.5`, {
                method: 'POST',
                credentials: 'include',
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
            var authResponse = await httpResponse.json();
            if (authResponse != null) {
                session.setItem('accType', accType);
                session.setItem('idToken', idToken);
                session.setItem('name', authResponse.name);
                session.setItem('email', authResponse.email);
                session.setItem('jwtToken', authResponse.jwtToken);
                wixLocationFrontend.to(`https://www.driprun.com.au/drip-run-app`);
            }
        }
        catch (err) {
            console.log('handleAuthApple -', err);
            return null;
        };
    };

    // const authResponse = await handleNotAdminAuth(accType, idToken);
    // var authResponse = userAuth(accType, idToken);
    // if (authResponse != null) {
    //     session.setItem('accType', accType);
    //     session.setItem('idToken', idToken);
    //     session.setItem('name', authResponse.name);
    //     session.setItem('email', authResponse.email);
    //     session.setItem('jwtToken', authResponse.jwtToken);
    //     wixLocationFrontend.to(`https://www.driprun.com.au/drip-run-app`);
    // }
    // else {
    //     toastHandler('Oops... Unauthorised',
    //                  'If you would like to become an member, please contact Drip Run.',
    //                  '',
    //                  '');
    // };

};

function toastHandler (title, content, deleteType, deleteId) {

    const toastInfo = {
        toastTitle: title,
        toastContent: content,
        deleteType: deleteType,
        deleteId: deleteId
    };
    wixWindowFrontend.openLightbox('Toast', toastInfo);

};
