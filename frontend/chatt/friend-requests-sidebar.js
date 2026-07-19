(function () {
  'use strict';


  var loading =
    false;


  function normalize(
    value
  ) {

    return String(
      value || ''
    )
      .trim()
      .toLowerCase();
  }


  async function readJson(
    response
  ) {

    try {

      return await response.json();

    } catch (
      error
    ) {

      return {};
    }
  }


  function showMessage(
    message,
    type
  ) {

    if (
      typeof window.showToast
      === 'function'
    ) {

      window.showToast(
        message,
        type || 'success'
      );
    }
  }


  function createAvatar(
    username
  ) {

    var image =
      document.createElement(
        'img'
      );


    image.className =
      'friend-request-avatar';


    image.alt =
      '';


    image.loading =
      'lazy';


    try {

      if (
        typeof window.makeAvatar
        === 'function'
      ) {

        window.makeAvatar(
          username,
          image
        );
      }

    } catch (
      error
    ) {
    }


    fetch(

      '/api/profile/public/'
      + encodeURIComponent(
        username
      ),

      {
        credentials:
          'include'
      }
    )

      .then(
        function (
          response
        ) {

          if (
            !response.ok
          ) {

            return null;
          }


          return response.json();
        }
      )

      .then(
        function (
          profile
        ) {

          if (
            !profile
          ) {

            return;
          }


          var avatarUrl =
            String(

              profile.avatarUrl

              || profile.avatar

              || profile.profilePictureUrl

              || ''

            )
              .trim();


          if (
            avatarUrl
          ) {

            image.src =
              avatarUrl;
          }
        }
      )

      .catch(
        function () {
        }
      );


    return image;
  }


  async function decideRequest(
    action,
    username,
    row
  ) {

    var buttons =
      row.querySelectorAll(
        'button'
      );


    buttons.forEach(
      function (
        button
      ) {

        button.disabled =
          true;
      }
    );


    row.classList.add(
      'is-loading'
    );


    try {

      var response =
        await fetch(

          '/api/friends/'
          + action,

          {
            method:
              'POST',

            credentials:
              'include',

            headers: {

              'Content-Type':
                'application/json',

              'X-Requested-With':
                'XMLHttpRequest'
            },

            body:
              JSON.stringify(
                {
                  username:
                    username
                }
              )
          }
        );


      var data =
        await readJson(
          response
        );


      if (
        !response.ok
      ) {

        throw new Error(

          data.message

          || (
            action === 'accept'

              ? 'Could not accept friend request.'

              : 'Could not decline friend request.'
          )
        );
      }


      if (
        action === 'accept'

        && typeof window.loadConversations
          === 'function'
      ) {

        await window.loadConversations();
      }


      if (
        action === 'accept'
      ) {

        showMessage(

          username
          + ' is now your friend.',

          'success'
        );


      } else {

        showMessage(

          'Friend request declined.',

          'success'
        );
      }


      row.classList.add(
        'is-removing'
      );


      setTimeout(
        function () {

          loadFriendRequestsSidebar();

        },

        160
      );


    } catch (
      error
    ) {

      row.classList.remove(
        'is-loading'
      );


      buttons.forEach(
        function (
          button
        ) {

          button.disabled =
            false;
        }
      );


      showMessage(

        error.message

        || 'Could not update friend request.',

        'error'
      );
    }
  }


  function createRequestRow(
    request
  ) {

    var username =
      normalize(
        request.from
      );


    var row =
      document.createElement(
        'div'
      );


    row.className =
      'friend-request-row';


    var user =
      document.createElement(
        'div'
      );


    user.className =
      'friend-request-user';


    user.appendChild(

      createAvatar(
        username
      )
    );


    var copy =
      document.createElement(
        'div'
      );


    copy.className =
      'friend-request-copy';


    var name =
      document.createElement(
        'div'
      );


    name.className =
      'friend-request-name';


    name.textContent =
      username;


    var subtitle =
      document.createElement(
        'div'
      );


    subtitle.className =
      'friend-request-subtitle';


    subtitle.textContent =
      'Wants to connect';


    copy.appendChild(
      name
    );


    copy.appendChild(
      subtitle
    );


    user.appendChild(
      copy
    );


    row.appendChild(
      user
    );


    var actions =
      document.createElement(
        'div'
      );


    actions.className =
      'friend-request-actions';


    var accept =
      document.createElement(
        'button'
      );


    accept.type =
      'button';


    accept.className =
      'friend-request-button accept';


    accept.textContent =
      'Accept';


    accept.onclick =
      function () {

        decideRequest(

          'accept',

          username,

          row
        );
      };


    var decline =
      document.createElement(
        'button'
      );


    decline.type =
      'button';


    decline.className =
      'friend-request-button decline';


    decline.textContent =
      'Decline';


    decline.onclick =
      function () {

        decideRequest(

          'decline',

          username,

          row
        );
      };


    actions.appendChild(
      accept
    );


    actions.appendChild(
      decline
    );


    row.appendChild(
      actions
    );


    return row;
  }


  async function loadFriendRequestsSidebar() {

    if (
      loading
    ) {

      return;
    }


    loading =
      true;


    var section =
      document.getElementById(
        'friendRequestsSection'
      );


    var list =
      document.getElementById(
        'friendRequestsList'
      );


    var count =
      document.getElementById(
        'friendRequestsCount'
      );


    if (
      !section
      || !list
      || !count
    ) {

      loading =
        false;


      return;
    }


    try {

      var response =
        await fetch(

          '/api/friends/requests',

          {
            credentials:
              'include'
          }
        );


      if (
        !response.ok
      ) {

        throw new Error(
          'Could not load friend requests.'
        );
      }


      var data =
        await readJson(
          response
        );


      var incoming =
        Array.isArray(
          data.incoming
        )

          ? data.incoming

          : [];


      list.innerHTML =
        '';


      if (
        incoming.length === 0
      ) {

        section.hidden =
          true;


        count.textContent =
          '0';


        return;
      }


      section.hidden =
        false;


      count.textContent =

        incoming.length > 99

          ? '99+'

          : String(
              incoming.length
            );


      incoming.forEach(
        function (
          request
        ) {

          list.appendChild(

            createRequestRow(
              request
            )
          );
        }
      );


    } catch (
      error
    ) {

      section.hidden =
        true;


    } finally {

      loading =
        false;
    }
  }


  window.loadFriendRequestsSidebar =
    loadFriendRequestsSidebar;



  function syncFriendDirectMessages() {

    if (
      typeof window.loadConversations
      === 'function'
    ) {

      window.loadConversations();
    }
  }


  function boot() {

    loadFriendRequestsSidebar();


    window.addEventListener(

      'focus',

      loadFriendRequestsSidebar
    );


    window.setInterval(

      loadFriendRequestsSidebar,

      30000
    );


    window.addEventListener(

      'focus',

      syncFriendDirectMessages
    );


    window.setInterval(

      syncFriendDirectMessages,

      10000
    );
  }


  if (
    document.readyState
    === 'loading'
  ) {

    document.addEventListener(

      'DOMContentLoaded',

      boot
    );


  } else {

    boot();
  }

})();
