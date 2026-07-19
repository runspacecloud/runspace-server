(function () {
  'use strict';


  var friendState = {
    friends: new Set(),
    incoming: new Set(),
    outgoing: new Set(),
    incomingUsers: []
  };


  function normalize(
    value
  ) {

    return String(
      value || ''
    )
      .trim()
      .toLowerCase();
  }


  function currentUsername() {

    try {

      if (
        typeof me !== 'undefined'
        && me
      ) {

        return normalize(
          me.username
        );
      }

    } catch (
      error
    ) {
    }


    return '';
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


  function clearState() {

    friendState
      .friends
      .clear();


    friendState
      .incoming
      .clear();


    friendState
      .outgoing
      .clear();


    friendState.incomingUsers =
      [];
  }


  async function loadFriendState() {

    var responses =
      await Promise.all([

        fetch(
          '/api/friends',

          {
            credentials:
              'include'
          }
        ),


        fetch(
          '/api/friends/requests',

          {
            credentials:
              'include'
          }
        )
      ]);


    if (
      !responses[0].ok
      || !responses[1].ok
    ) {

      throw new Error(
        'Could not load friend status.'
      );
    }


    var friendsData =
      await readJson(
        responses[0]
      );


    var requestsData =
      await readJson(
        responses[1]
      );


    clearState();


    (
      friendsData.friends
      || []
    )
      .forEach(
        function (
          friend
        ) {

          var username =
            normalize(
              friend.username
            );


          if (
            username
          ) {

            friendState
              .friends
              .add(
                username
              );
          }
        }
      );


    (
      requestsData.incoming
      || []
    )
      .forEach(
        function (
          request
        ) {

          var username =
            normalize(
              request.from
            );


          if (
            username
          ) {

            friendState
              .incoming
              .add(
                username
              );


            if (
              !friendState
                .incomingUsers
                .includes(
                  username
                )
            ) {

              friendState
                .incomingUsers
                .push(
                  username
                );
            }
          }
        }
      );


    (
      requestsData.outgoing
      || []
    )
      .forEach(
        function (
          request
        ) {

          var username =
            normalize(
              request.to
            );


          if (
            username
          ) {

            friendState
              .outgoing
              .add(
                username
              );
          }
        }
      );
  }


  function relationFor(
    username
  ) {

    var key =
      normalize(
        username
      );


    if (
      friendState
        .friends
        .has(
          key
        )
    ) {

      return 'friends';
    }


    if (
      friendState
        .incoming
        .has(
          key
        )
    ) {

      return 'incoming';
    }


    if (
      friendState
        .outgoing
        .has(
          key
        )
    ) {

      return 'outgoing';
    }


    return 'none';
  }


  function setStatus(
    status,
    message,
    type
  ) {

    status.textContent =
      message || '';


    status.className =
      'rs-friend-status';


    if (
      type
    ) {

      status.classList.add(
        'is-' + type
      );
    }
  }


  function openFriendChat(
    username
  ) {

    window.closeNewDM();


    if (
      typeof openChat
      === 'function'
    ) {

      openChat(
        username
      );
    }
  }


  async function sendFriendRequest(
    username,
    button,
    status
  ) {

    button.disabled =
      true;


    button.textContent =
      'Sending...';


    setStatus(
      status,
      '',
      ''
    );


    try {

      var response =
        await fetch(

          '/api/friends/request',

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


      var key =
        normalize(
          username
        );


      if (
        response.ok
      ) {

        if (
          data.status
          === 'already_friends'
        ) {

          friendState
            .friends
            .add(
              key
            );


          friendState
            .incoming
            .delete(
              key
            );


          friendState
            .outgoing
            .delete(
              key
            );


          setStatus(

            status,

            'You are already friends.',

            'success'
          );


        } else {

          friendState
            .outgoing
            .add(
              key
            );


          setStatus(

            status,

            'Friend request sent.',

            'success'
          );
        }


        renderAction(
          username,
          button,
          status
        );


        return;
      }


      if (
        data.code
        === 'incoming_request_exists'
      ) {

        friendState
          .incoming
          .add(
            key
          );


        setStatus(

          status,

          'This user already sent you a friend request.',

          'info'
        );


        renderAction(
          username,
          button,
          status
        );


        return;
      }


      throw new Error(

        data.message
        || 'Could not send friend request.'
      );


    } catch (
      error
    ) {

      button.disabled =
        false;


      button.textContent =
        'Send friend request';


      setStatus(

        status,

        error.message
        || 'Could not send friend request.',

        'error'
      );
    }
  }


  async function acceptFriendRequest(
    username,
    button,
    status
  ) {

    button.disabled =
      true;


    button.textContent =
      'Accepting...';


    setStatus(
      status,
      '',
      ''
    );


    try {

      var response =
        await fetch(

          '/api/friends/accept',

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
          || 'Could not accept friend request.'
        );
      }


      var key =
        normalize(
          username
        );


      friendState
        .incoming
        .delete(
          key
        );


      friendState.incomingUsers =
        friendState
          .incomingUsers
          .filter(
            function (
              username
            ) {

              return username
                !== key;
            }
          );


      friendState
        .outgoing
        .delete(
          key
        );


      friendState
        .friends
        .add(
          key
        );


      setStatus(

        status,

        'Friend request accepted.',

        'success'
      );


      renderAction(
        username,
        button,
        status
      );


    } catch (
      error
    ) {

      button.disabled =
        false;


      button.textContent =
        'Accept friend request';


      setStatus(

        status,

        error.message
        || 'Could not accept friend request.',

        'error'
      );
    }
  }


  function renderAction(
    username,
    button,
    status
  ) {

    var relation =
      relationFor(
        username
      );


    button.className =
      'rs-friend-action';


    button.disabled =
      false;


    button.onclick =
      null;


    if (
      relation
      === 'friends'
    ) {

      button.textContent =
        'Open chat';


      button.classList.add(
        'is-primary'
      );


      button.onclick =
        function () {

          openFriendChat(
            username
          );
        };


      return;
    }


    if (
      relation
      === 'incoming'
    ) {

      button.textContent =
        'Accept friend request';


      button.classList.add(
        'is-accept'
      );


      button.onclick =
        function () {

          acceptFriendRequest(

            username,

            button,

            status
          );
        };


      return;
    }


    if (
      relation
      === 'outgoing'
    ) {

      button.textContent =
        'Request pending';


      button.disabled =
        true;


      button.classList.add(
        'is-pending'
      );


      return;
    }


    button.textContent =
      'Send friend request';


    button.classList.add(
      'is-primary'
    );


    button.onclick =
      function () {

        sendFriendRequest(

          username,

          button,

          status
        );
      };
  }


  function loadRealAvatar(
    username,
    image
  ) {

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
            !avatarUrl
          ) {

            return;
          }


          image.src =
            avatarUrl;
        }
      )

      .catch(
        function () {
        }
      );
  }


function createAvatar(
    username
  ) {

    var image =
      document.createElement(
        'img'
      );


    image.alt =
      '';


    image.loading =
      'lazy';


    image.className =
      'rs-friend-avatar';


    try {

      if (
        typeof makeAvatar
        === 'function'
      ) {

        makeAvatar(
          username,
          image
        );
      }

    } catch (
      error
    ) {
    }


    loadRealAvatar(
      username,
      image
    );


    return image;
  }



  function createIncomingRequestRow(
    username
  ) {

    var row =
      document.createElement(
        'div'
      );


    row.className =
      'modal-user rs-friend-result';


    row.appendChild(

      createAvatar(
        username
      )
    );


    var copy =
      document.createElement(
        'div'
      );


    copy.className =
      'rs-friend-user-copy';


    var name =
      document.createElement(
        'div'
      );


    name.className =
      'modal-user-name';


    name.textContent =
      username;


    var status =
      document.createElement(
        'div'
      );


    status.className =
      'rs-friend-status is-info';


    status.textContent =
      'Sent you a friend request';


    copy.appendChild(
      name
    );


    copy.appendChild(
      status
    );


    row.appendChild(
      copy
    );


    var button =
      document.createElement(
        'button'
      );


    button.type =
      'button';


    renderAction(

      username,

      button,

      status
    );


    row.appendChild(
      button
    );


    return row;
  }


  function renderIdleState() {

    var results =
      document.getElementById(
        'newDMResults'
      );


    results.innerHTML =
      '';


    if (
      friendState
        .incomingUsers
        .length === 0
    ) {

      results.innerHTML =

        '<div class="rs-friend-empty">'
        + 'Enter an exact username.'
        + '</div>';


      return;
    }


    var title =
      document.createElement(
        'div'
      );


    title.className =
      'rs-friend-section-title';


    title.textContent =
      'Friend requests';


    results.appendChild(
      title
    );


    friendState
      .incomingUsers
      .forEach(
        function (
          username
        ) {

          results.appendChild(

            createIncomingRequestRow(
              username
            )
          );
        }
      );
  }


  function renderTypedUsername(
    value
  ) {

    var results =
      document.getElementById(
        'newDMResults'
      );


    var username =
      normalize(
        value
      );


    results.innerHTML =
      '';


    if (
      username.length < 2
    ) {

      renderIdleState();


      return;
    }


    var row =
      document.createElement(
        'div'
      );


    row.className =
      'modal-user rs-friend-result';


    row.appendChild(

      createAvatar(
        username
      )
    );


    var copy =
      document.createElement(
        'div'
      );


    copy.className =
      'rs-friend-user-copy';


    var name =
      document.createElement(
        'div'
      );


    name.className =
      'modal-user-name';


    name.textContent =
      username;


    var status =
      document.createElement(
        'div'
      );


    status.className =
      'rs-friend-status';


    copy.appendChild(
      name
    );


    copy.appendChild(
      status
    );


    row.appendChild(
      copy
    );


    var button =
      document.createElement(
        'button'
      );


    button.type =
      'button';


    if (
      username
      === currentUsername()
    ) {

      button.className =
        'rs-friend-action is-pending';


      button.textContent =
        'This is you';


      button.disabled =
        true;


    } else {

      renderAction(

        username,

        button,

        status
      );
    }


    row.appendChild(
      button
    );


    results.appendChild(
      row
    );
  }


  window.openNewDM =
    async function () {

      var modal =
        document.getElementById(
          'newDMModal'
        );


      var input =
        document.getElementById(
          'newDMSearch'
        );


      var results =
        document.getElementById(
          'newDMResults'
        );


      modal.classList.add(
        'show'
      );


      input.placeholder =
        'Enter exact username...';


      input.focus();


      results.innerHTML =

        '<div class="rs-friend-loading">'
        + 'Loading friend status...'
        + '</div>';


      try {

        await loadFriendState();


      } catch (
        error
      ) {

        clearState();
      }


      renderTypedUsername(
        input.value
      );
    };


  window.closeNewDM =
    function () {

      document
        .getElementById(
          'newDMModal'
        )
        .classList
        .remove(
          'show'
        );


      document
        .getElementById(
          'newDMResults'
        )
        .innerHTML =
        '';


      document
        .getElementById(
          'newDMSearch'
        )
        .value =
        '';
    };


  window.searchUsers =
    function (
      value
    ) {

      renderTypedUsername(
        value
      );
    };


  window.startDM =
    function (
      peer
    ) {

      if (
        relationFor(
          peer
        )
        === 'friends'
      ) {

        openFriendChat(
          peer
        );
      }
    };

})();
